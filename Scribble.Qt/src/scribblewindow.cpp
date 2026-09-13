#include "scribblewindow.h"

#include <QApplication>
#include <QGuiApplication>
#include <QPainter>
#include <QPaintEvent>
#include <QResizeEvent>
#include <QShowEvent>
#include <QTabletEvent>
#include <QVBoxLayout>

#include <algorithm>
#include <cmath>

// After the Qt headers, deliberately. <windows.h> defines macros that break them, and the
// CMakeLists already sets NOMINMAX and WIN32_LEAN_AND_MEAN for the same reason.
// Qt defines `emit` as an empty macro so that `emit signalName()` reads as syntax. The self
// test report has a method called emit(), which that macro turns into `r.()`. Undefining it
// here rather than building with QT_NO_KEYWORDS keeps `signals:` working in the header; the
// cost is that this file says Q_EMIT where it would otherwise say emit.
#undef emit

#include "qtbackend.h"

#include "selftest.h"

namespace {

/// Qt reports pressure normalised to 0..1 and never says what the device's own range was, so
/// the raw column is reconstructed against this. The recording header states it for the same
/// reason: a number with no stated scale is not a measurement.
constexpr int kAssumedMaxPressure = 1024;

/// The same conversion WinPenKit applies, so the two report comparable numbers rather than two
/// defensible ones. A linear fall-off from 90 degrees, not the trigonometric form.
void tiltToSpherical(double tiltX, double tiltY, double& azimuth, double& altitude) {
    const double mag = std::sqrt(tiltX * tiltX + tiltY * tiltY);
    altitude = std::clamp(90.0 - mag, 0.0, 90.0);

    if (mag > 0.5) {
        const double deg = std::atan2(-tiltX, tiltY) * 180.0 / 3.14159265358979323846;
        azimuth = std::fmod(std::fmod(deg, 360.0) + 360.0, 360.0);
    } else {
        azimuth = 0.0;
    }
}

} // namespace

// ── CanvasWidget ────────────────────────────────────────────────

CanvasWidget::CanvasWidget(QWidget* parent) : QWidget(parent) {
    setAttribute(Qt::WA_OpaquePaintEvent);
    setAttribute(Qt::WA_TabletTracking);   // hover points as well as contact
    setAutoFillBackground(false);
}

void CanvasWidget::ensureImage() {
    const double dpr = devicePixelRatioF();
    const int w = int(std::ceil(width() * dpr));
    const int h = int(std::ceil(height() * dpr));
    if (w <= 0 || h <= 0) return;
    if (m_image.width() == w && m_image.height() == h) return;

    QImage next(w, h, QImage::Format_ARGB32_Premultiplied);
    next.setDevicePixelRatio(dpr);
    next.fill(QColor(0xF0, 0xF0, 0xF0));

    // Existing ink is kept at its own pixel size rather than scaled, so growing the window
    // does not resample every stroke already drawn.
    if (!m_image.isNull()) {
        QPainter p(&next);
        p.setRenderHint(QPainter::SmoothPixmapTransform, false);
        p.drawImage(QPoint(0, 0), m_image);
    }

    m_image = next;
}

QPointF CanvasWidget::surfaceOriginPx() const {
    const double dpr = devicePixelRatioF();
    const QPointF logical = const_cast<CanvasWidget*>(this)->mapToGlobal(QPointF(0.0, 0.0));
    return QPointF(logical.x() * dpr, logical.y() * dpr);
}

QPointF CanvasWidget::desktopToCanvas(double x, double y) const {
    const QPointF o = surfaceOriginPx();
    return QPointF(x - o.x(), y - o.y());
}

void CanvasWidget::clear() {
    if (m_image.isNull()) return;
    m_image.fill(QColor(0xF0, 0xF0, 0xF0));
    m_lastCanvasPx.reset();
    update();
}

void CanvasWidget::setInProximity(bool in) {
    m_inProximity = in;
    m_readout.inProximity = in;
    if (!in) {
        // Leaving proximity ends the stroke. Without this a pen lifted off and set down
        // elsewhere draws a line across the gap.
        m_inContact = false;
        m_lastCanvasPx.reset();
    }
    if (m_readout.hasData) Q_EMIT readoutChanged(m_readout);
}

void CanvasWidget::fillSurfaceRect(int x, int y, int size, int r, int g, int b) {
    ensureImage();
    if (m_image.isNull()) return;
    QPainter p(&m_image);
    p.setRenderHint(QPainter::Antialiasing, false);
    // The painter inherits the image's device pixel ratio, so a rect given in image pixels
    // would be scaled by it. The markers are defined in surface pixels, so that is undone.
    p.scale(1.0 / m_image.devicePixelRatio(), 1.0 / m_image.devicePixelRatio());
    p.fillRect(QRect(x, y, size, size), QColor(r, g, b));
}

void CanvasWidget::drawSegment(const QPointF& fromPx, const QPointF& toPx, double pressure) {
    if (m_image.isNull()) return;

    QPainter p(&m_image);
    p.setRenderHint(QPainter::Antialiasing, true);
    p.scale(1.0 / m_image.devicePixelRatio(), 1.0 / m_image.devicePixelRatio());

    // Width in surface pixels, so a 6px brush is 6 physical pixels on every display. Taking
    // the brush as a logical width would draw 2.25 times wider here than on an unscaled
    // monitor, which is the fault issue 38 measured across the other samples.
    const double w = m_brushWidth * std::max(0.15, pressure);
    QPen pen(QColor(20, 20, 20));
    pen.setWidthF(w);
    pen.setCapStyle(Qt::RoundCap);
    pen.setJoinStyle(Qt::RoundJoin);
    p.setPen(pen);
    p.drawLine(fromPx, toPx);
}

/// Qt's `QInputEvent::timestamp` is a `quint64`, and on Windows it is not a 64-bit clock.
/// The platform plugin fills it from `GetMessageTime`, which is 32 bits, and widens the
/// result -- so the value has already wrapped by the time it is a quint64, and a recording
/// spanning the boundary carries a difference wrong by 49.7 days.
///
/// Recovered rather than detected: the reading tracks GetTickCount64, so the full value is the
/// nearest multiple of 2^32 ms that makes the two agree. Stateless, so an idle session, a
/// dropped packet or a first packet after the wrap all behave the same.
///
/// The same arithmetic as WinPenKit's PenTimestamp.FromSystemTicks. Duplicated rather than
/// shared because this sample links no WinPenKit header, which is the point of it.
///
/// Resolution, measured on a Wacom DTH246 on 13 Sep 2026: 15.6ms, the coarsest of the seven
/// samples. Across 809 gaps in one stroke the smallest is 15ms -- 504 of 16ms, 303 of 15ms --
/// and nothing finer occurs.
///
/// So 2280 points carried 810 distinct timestamps. That is not WPF's batching: this handler
/// records one point per event, and QTabletEvent is a QSinglePointEvent, so all 2280 are
/// separate events. They repeat because the clock advances on the timer tick while the tablet
/// reports at 180 Hz. Recorded in testdata/qt-hardware-stroke.csv.
static int64_t anchorToSystemTicks(quint64 rawMs) {
    constexpr int64_t range = 1LL << 32;
    const int64_t raw = static_cast<int64_t>(rawMs);
    const int64_t now = static_cast<int64_t>(::GetTickCount64());
    const double k = std::floor((static_cast<double>(now - raw) + range / 2.0) / range);
    return raw + static_cast<int64_t>(k) * range;
}

void CanvasWidget::tabletEvent(QTabletEvent* event) {
    event->accept();
    ensureImage();

    const double dpr = devicePixelRatioF();

    // globalPosition is device independent pixels and fractional. Multiplying by the device
    // pixel ratio is the whole conversion; there is no integer point type on this path, which
    // is the difference between Qt and the framework APIs that quantize.
    const QPointF globalLogical = event->globalPosition();
    const QPointF desktopPx(globalLogical.x() * dpr, globalLogical.y() * dpr);
    const QPointF canvasPx = desktopToCanvas(desktopPx.x(), desktopPx.y());

    const double pressure = event->pressure();

    switch (event->type()) {
        case QEvent::TabletPress:
            m_inContact = true;
            m_lastCanvasPx = canvasPx;
            break;

        case QEvent::TabletMove:
            if (m_inContact) {
                if (m_lastCanvasPx) drawSegment(*m_lastCanvasPx, canvasPx, pressure);
                m_lastCanvasPx = canvasPx;
                update();
            } else {
                // Hover. The last point is dropped so the next contact does not draw a line
                // from wherever the pen left the surface.
                m_lastCanvasPx.reset();
            }
            break;

        case QEvent::TabletRelease:
            m_inContact = false;
            m_lastCanvasPx.reset();
            break;

        default:
            break;
    }

    // After the switch, and gated on contact rather than on pressure: the release event that
    // just cleared m_inContact reports pressure 0.5 and the same position as the point before
    // it, so a pressure test appends a duplicate at a pressure the stroke never had.
    if (!m_recordPath.empty() && m_inContact) {
        m_recorded.push_back({desktopPx.x(), desktopPx.y(),
                              std::round(pressure * kAssumedMaxPressure),
                              static_cast<double>(anchorToSystemTicks(event->timestamp()))});
    }

    // The window origin, read per event rather than cached: a window that moves must not keep
    // reporting where it used to be, which is the fault L3.origin-tracks-window exists for.
    const QPointF windowOriginLogical = window()->mapToGlobal(QPointF(0.0, 0.0));

    PenReadout r;
    r.hasData = true;
    r.inProximity = m_inProximity || pressure > 0.0;

    r.screenX = desktopPx.x();
    r.screenY = desktopPx.y();
    r.appX = desktopPx.x() - windowOriginLogical.x() * dpr;
    r.appY = desktopPx.y() - windowOriginLogical.y() * dpr;
    r.canvasX = canvasPx.x();
    r.canvasY = canvasPx.y();

    // Zero once the pen is off the surface, rather than Qt's 0.5. The release event carries a
    // default pressure, so reporting it leaves every finished stroke reading half pressure --
    // a value the pen never applied, and one the other samples never show, since their APIs
    // report 0 on release. Same reason the recorder above gates on contact.
    const double reported = m_inContact ? pressure : 0.0;
    r.normalisedPressure = reported;
    r.rawPressure = int(std::round(reported * kAssumedMaxPressure));
    r.maxPressure = kAssumedMaxPressure;

    r.tiltX = event->xTilt();
    r.tiltY = event->yTilt();
    tiltToSpherical(r.tiltX, r.tiltY, r.azimuth, r.altitude);
    r.twist = event->rotation();

    const bool eraserTip = event->pointerType() == QPointingDevice::PointerType::Eraser;
    r.cursor = eraserTip ? QStringLiteral("Eraser") : QStringLiteral("Pen");

    // Qt reports a mouse-button mask rather than the driver's own encoding. The tip is the
    // left button and the barrel switches follow as right and middle. There is no third: a pen
    // with three barrel switches reports only two through this API, so B3 can never light
    // here while it can on the Wintab samples. Shown anyway, because a dot that never lights
    // is a fact about Qt worth seeing next to the samples where it does.
    const Qt::MouseButtons b = event->buttons();
    r.tip = b.testFlag(Qt::LeftButton) || m_inContact;
    r.eraser = eraserTip && m_inContact;
    r.barrel1 = b.testFlag(Qt::RightButton);
    r.barrel2 = b.testFlag(Qt::MiddleButton);
    r.barrel3 = false;
    r.rawButtons = unsigned(b.toInt());

    m_readout = r;
    Q_EMIT readoutChanged(r);
}

void CanvasWidget::paintEvent(QPaintEvent* event) {
    QPainter p(this);
    if (m_image.isNull()) {
        p.fillRect(event->rect(), QColor(0xF0, 0xF0, 0xF0));
        return;
    }
    // The image carries the device pixel ratio, so this is a 1:1 blit rather than a scale.
    p.drawImage(QPoint(0, 0), m_image);
}

void CanvasWidget::resizeEvent(QResizeEvent*) {
    ensureImage();
}

// ── ScribbleWindow ──────────────────────────────────────────────

ScribbleWindow::ScribbleWindow(PenApi requested, PenApi obtained, QWidget* parent)
    : QMainWindow(parent), m_requested(requested), m_obtained(obtained) {
    setWindowTitle(QStringLiteral("Scribble Qt - WinPenKit comparison"));
    resize(1200, 700);

    auto* central = new QWidget(this);
    auto* column = new QVBoxLayout(central);
    column->setContentsMargins(0, 0, 0, 0);
    column->setSpacing(0);

    m_ribbon = new ScribbleRibbon(obtained, central);
    m_canvas = new CanvasWidget(central);
    m_canvas->setBrushWidth(m_ribbon->brushSize());

    connect(m_canvas, &CanvasWidget::readoutChanged,
            m_ribbon, &ScribbleRibbon::setReadout);
    connect(m_ribbon, &ScribbleRibbon::clearClicked,
            m_canvas, &CanvasWidget::clear);
    connect(m_ribbon, &ScribbleRibbon::brushSizeChanged,
            m_canvas, &CanvasWidget::setBrushWidth);

    // The window performs the switch and tells the ribbon what came of it, so the dropdown
    // shows the backend Qt reports rather than the one that was asked for.
    connect(m_ribbon, &ScribbleRibbon::apiSelected, this, [this](PenApi chosen) {
        const bool ok = qtbackend::select(chosen);
        m_obtained = qtbackend::current();
        m_ribbon->setActiveApi(m_obtained, ok);
    });

    column->addWidget(m_ribbon);
    column->addWidget(m_canvas, 1);
    setCentralWidget(central);

    // Proximity is delivered to the application, not to a widget, so it is caught here.
    qApp->installEventFilter(this);
}

void ScribbleWindow::snapRibbonHeight() {
    // Qt lays widgets out in whole device independent pixels, and at a fractional device pixel
    // ratio a whole logical height is not a whole device height: the ribbon wanted 211 logical
    // px, which at 2.25 put the canvas origin at y=475.25 and made the framework resample the
    // entire surface to draw it between pixel rows. L1.surface-alignment caught it.
    //
    // So the ribbon is grown to the next logical height whose product with the ratio is whole
    // -- a multiple of 4 at 2.25, of 2 at 1.5, any integer at 2. Growing rather than shrinking,
    // because shrinking would clip what the ribbon needs to show.
    const double dpr = devicePixelRatioF();
    const int needed = m_ribbon->sizeHint().height();

    for (int h = needed; h < needed + 32; h++) {
        const double device = h * dpr;
        if (std::abs(device - std::round(device)) < 1e-6) {
            m_ribbon->setFixedHeight(h);
            return;
        }
    }
    m_ribbon->setFixedHeight(needed);   // no whole height nearby; leave it as it was
}

void ScribbleWindow::showEvent(QShowEvent* event) {
    QMainWindow::showEvent(event);
    snapRibbonHeight();
}

void ScribbleWindow::resizeEvent(QResizeEvent* event) {
    QMainWindow::resizeEvent(event);
    // Also covers a move to a monitor with a different scale, which changes the ratio the
    // height was snapped against.
    snapRibbonHeight();
}

bool ScribbleWindow::eventFilter(QObject* watched, QEvent* event) {
    if (event->type() == QEvent::TabletEnterProximity) {
        m_canvas->setInProximity(true);
    } else if (event->type() == QEvent::TabletLeaveProximity) {
        m_canvas->setInProximity(false);
    }
    return QMainWindow::eventFilter(watched, event);
}

void ScribbleWindow::saveRecording() {
    if (m_canvas->recordPath().empty()) return;

    // Written through the same recorder Scribble.Win32 uses, so the header and the number
    // formatting are not a second implementation of the format that --replay reads.
    const QByteArray source = penapi::description(m_obtained).toUtf8();
    selftest::Recorder rec;
    // The clock depends on which backend Qt is actually running, so it is read from the
    // obtained backend rather than hardcoded. On WM_POINTER the timestamp comes from the
    // window message and tracks GetTickCount64, measured; on WinTab Qt takes it from the
    // driver packet, which is a different clock with an origin nobody here has established.
    // Naming both SystemTicks would give the same Wintab clock different provenance depending
    // on which sample recorded it.
    rec.describe(("Qt " + source).constData(), kAssumedMaxPressure,
                 m_obtained == PenApi::WinTab ? selftest::TimestampSource::DeviceTicks
                                              : selftest::TimestampSource::SystemTicks);
    for (const auto& p : m_canvas->recorded())
        rec.add(p[0], p[1], static_cast<uint32_t>(p[2]),
                static_cast<int64_t>(p[3]) * 1000LL);

    const int written = rec.save(m_canvas->recordPath());
    fprintf(stderr, "[record] %d points -> %s\n", written, m_canvas->recordPath().c_str());
}

int ScribbleWindow::runSelfTest(const std::string& replayPath) {
    selftest::Report r("Scribble.Qt");

    const HWND hwnd = reinterpret_cast<HWND>(winId());
    const double dpr = m_canvas->devicePixelRatioF();
    const QSize surface = m_canvas->surfaceSize();
    const QPointF origin = m_canvas->surfaceOriginPx();

    r.check_dpi_awareness();
    r.check_window_placement(hwnd);
    r.report_scale(dpr);

    // Which pen API this run is actually on, and whether that is the one asked for.
    //
    // This was a report and is now a check, because a report was not enough. It printed the
    // requested backend and nothing anywhere compared it against the obtained one -- so for a
    // day this sample said "Wintab, tablet-native" while running on WM_POINTER, and the
    // recordings it produced carry that label. An instrument that cannot detect what it is
    // pointed at is worse than none, because it is believed.
    const PenApi live = qtbackend::current();
    const bool matched = qtbackend::available() && live == m_requested;
    r.check("L0.pen-api", matched,
            (QStringLiteral("requested %1, obtained %2%3")
                 .arg(penapi::description(m_requested), penapi::description(live),
                      matched ? QString()
                              : QStringLiteral("  <- Qt did not give the backend that was asked "
                                               "for; anything recorded on this run is on the "
                                               "one it did give"))
             ).toUtf8().constData());

    // Qt lays out in device independent pixels, so the logical-to-physical ratio is the
    // device pixel ratio -- the same relationship WPF and Avalonia have, and not the 1.0 that
    // Win32 and WinForms report.
    r.check_surface_physical(surface.width(), surface.height(),
                             m_canvas->width(), m_canvas->height(), dpr);

    r.check_surface_alignment(origin.x(), origin.y());

    r.check_presentation_1to1(surface.width(), surface.height(),
                              m_canvas->width() * dpr, m_canvas->height() * dpr);

    auto desktop_to_canvas = [this](double x, double y) {
        const QPointF p = m_canvas->desktopToCanvas(x, y);
        return std::make_pair(p.x(), p.y());
    };

    if (!replayPath.empty()) {
        auto input = selftest::load_recording(replayPath);
        if (input.empty()) {
            r.skip("L2.recording-subpixel", "recording empty or unreadable");
        } else {
            selftest::center_on(input, origin.x(), origin.y(),
                                surface.width(), surface.height());

            std::vector<std::pair<double, double>> out;
            out.reserve(input.size());
            for (const auto& pt : input)
                out.push_back(desktop_to_canvas(pt.first, pt.second));

            r.check_recording_subpixel(input);
            r.check_conversion_snap(out, 1.0);
            r.check_conversion_lossless(input, out);
        }
    }

    // Measures what reached the screen, so it runs before the check below moves the window.
    if (surface.width() > 0 && surface.height() > 0) {
        selftest::PresentationProbe probe(surface.width(), surface.height());

        m_canvas->clear();
        probe.draw_with([this](int x, int y, int size, int rr, int gg, int bb) {
            m_canvas->fillSurfaceRect(x, y, size, rr, gg, bb);
        });
        m_canvas->repaint();

        // Qt's own loop, not the Win32 pump the header defaults to: a Qt widget repaints from
        // a queued event, and pumping only the Win32 queue would leave that undelivered.
        probe.measure(r, hwnd, 5000,
                      [] { QCoreApplication::processEvents(QEventLoop::AllEvents, 20); });
    } else {
        r.skip("L1.presentation-sampling", "no drawing surface");
    }

    r.check_origin_tracks_window(hwnd, desktop_to_canvas, 1.0);

    return r.emit();
}
