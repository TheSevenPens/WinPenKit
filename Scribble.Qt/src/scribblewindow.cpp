#include "scribblewindow.h"

#include <QApplication>
#include <QGuiApplication>
#include <QHBoxLayout>
#include <QLabel>
#include <QPainter>
#include <QPaintEvent>
#include <QResizeEvent>
#include <QScreen>
#include <QTabletEvent>
#include <QVBoxLayout>
#include <QWindow>

#include <cmath>

// After the Qt headers, deliberately. <windows.h> defines macros that break them, and the
// CMakeLists already sets NOMINMAX and WIN32_LEAN_AND_MEAN for the same reason.
// Qt defines `emit` as an empty macro so that `emit signalName()` reads as syntax. The self
// test report has a method called emit(), which that macro turns into `r.()`. Undefining it
// here rather than building with QT_NO_KEYWORDS keeps `signals:` working in the header; the
// cost is that this file says Q_EMIT where it would otherwise say emit.
#undef emit

#include "selftest.h"

namespace {

/// Qt reports position in device independent pixels and pressure normalised to 0..1. Every
/// other sample in this repository reports physical desktop pixels and raw pressure against a
/// stated maximum, so both are converted here rather than at each use.
constexpr int kAssumedMaxPressure = 1024;

const char* backendName(PenBackend b) {
    return b == PenBackend::WinTab ? "Qt WinTab (tablet-native)" : "Qt WM_Pointer";
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
                              std::round(pressure * kAssumedMaxPressure)});
    }

    const char* cursor =
        event->pointerType() == QPointingDevice::PointerType::Eraser ? "eraser" : "pen";

    Q_EMIT telemetryChanged(
        QStringLiteral("Screen: %1, %2   Canvas: %3, %4   Pressure: %5 / %6   "
                       "Tilt: %7, %8   Twist: %9   Cursor: %10")
            .arg(desktopPx.x(), 0, 'f', 2).arg(desktopPx.y(), 0, 'f', 2)
            .arg(canvasPx.x(), 0, 'f', 2).arg(canvasPx.y(), 0, 'f', 2)
            .arg(std::round(pressure * kAssumedMaxPressure)).arg(kAssumedMaxPressure)
            .arg(event->xTilt()).arg(event->yTilt())
            .arg(event->rotation(), 0, 'f', 1)
            .arg(QString::fromLatin1(cursor)));
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

ScribbleWindow::ScribbleWindow(PenBackend backend, QWidget* parent)
    : QMainWindow(parent), m_backend(backend) {
    setWindowTitle(QStringLiteral("Scribble Qt - no WinPenKit"));
    resize(1200, 700);

    auto* central = new QWidget(this);
    auto* column = new QVBoxLayout(central);
    column->setContentsMargins(0, 0, 0, 0);
    column->setSpacing(0);

    auto* ribbon = new QWidget(central);
    auto* row = new QHBoxLayout(ribbon);
    row->setContentsMargins(12, 8, 12, 8);

    // Stated rather than discovered. Qt settles the backend during platform plugin
    // initialisation and offers the application no way to ask afterwards, so the only honest
    // thing to show is what this process asked for at startup.
    m_backendLabel = new QLabel(QStringLiteral("PEN API: %1  (fixed at startup)")
                                    .arg(QString::fromLatin1(backendName(backend))), ribbon);
    row->addWidget(m_backendLabel);
    row->addStretch();

    m_telemetry = new QLabel(QStringLiteral("Screen: --   Canvas: --   Pressure: --"), ribbon);
    row->addWidget(m_telemetry);

    m_canvas = new CanvasWidget(central);
    connect(m_canvas, &CanvasWidget::telemetryChanged,
            m_telemetry, &QLabel::setText);

    column->addWidget(ribbon);
    column->addWidget(m_canvas, 1);
    setCentralWidget(central);
}

void ScribbleWindow::saveRecording() {
    if (m_canvas->recordPath().empty()) return;

    // Written through the same recorder Scribble.Win32 uses, so the header and the number
    // formatting are not a second implementation of the format that --replay reads.
    selftest::Recorder rec;
    rec.describe(backendName(m_backend), kAssumedMaxPressure);
    for (const auto& p : m_canvas->recorded())
        rec.add(p[0], p[1], static_cast<uint32_t>(p[2]));

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
