#include "ribbon.h"

#include <QFont>
#include <QFrame>
#include <QGridLayout>
#include <QHBoxLayout>
#include <QVBoxLayout>

namespace {

const char* kHeaderStyle = "color:#555; font-weight:600; letter-spacing:1px;";
const char* kLabelStyle  = "color:#777;";
const char* kValueStyle  = "color:#222;";

QLabel* header(const QString& text) {
    auto* l = new QLabel(text);
    l->setStyleSheet(QString::fromLatin1(kHeaderStyle));
    return l;
}

QLabel* caption(const QString& text) {
    auto* l = new QLabel(text);
    l->setStyleSheet(QString::fromLatin1(kLabelStyle));
    return l;
}

QFrame* separator() {
    auto* f = new QFrame;
    f->setFrameShape(QFrame::VLine);
    f->setFrameShadow(QFrame::Plain);
    f->setStyleSheet(QStringLiteral("color:#d8d8d8;"));
    return f;
}

} // namespace

QLabel* ScribbleRibbon::makeValue(const QString& initial) {
    auto* l = new QLabel(initial);
    l->setStyleSheet(QString::fromLatin1(kValueStyle));
    // Monospaced digits, so a value changing from 9 to 10 does not shift everything after it.
    QFont f = l->font();
    f.setStyleHint(QFont::Monospace);
    f.setFamily(QStringLiteral("Consolas"));
    l->setFont(f);
    return l;
}

QLabel* ScribbleRibbon::makeDot() {
    auto* d = new QLabel(QStringLiteral("●"));
    setDot(d, false);
    return d;
}

void ScribbleRibbon::setDot(QLabel* dot, bool on, bool eraserColour) {
    const char* colour = !on ? "#b4b4b4" : (eraserColour ? "#dc3c1e" : "#008c00");
    dot->setStyleSheet(QStringLiteral("color:%1;").arg(QString::fromLatin1(colour)));
}

QWidget* ScribbleRibbon::makeSection(const QString& text, QWidget* body) {
    auto* w = new QWidget;
    auto* v = new QVBoxLayout(w);
    v->setContentsMargins(0, 0, 0, 0);
    v->setSpacing(3);
    v->addWidget(header(text));
    v->addWidget(body);
    v->addStretch();
    return w;
}

ScribbleRibbon::ScribbleRibbon(PenApi active, QWidget* parent)
    : QWidget(parent), m_active(active) {
    setStyleSheet(QStringLiteral("background:#f5f5f5;"));

    auto* row = new QHBoxLayout(this);
    row->setContentsMargins(14, 8, 14, 8);
    row->setSpacing(14);

    // ── PEN API ──────────────────────────────────────────────
    m_api = new QComboBox;
    m_api->addItem(penapi::label(PenApi::WmPointer));
    m_api->addItem(penapi::label(PenApi::WinTab));
    m_api->setCurrentIndex(active == PenApi::WinTab ? 1 : 0);
    connect(m_api, &QComboBox::currentIndexChanged, this, &ScribbleRibbon::onApiSelected);

    m_clear = new QPushButton(QStringLiteral("Clear"));
    connect(m_clear, &QPushButton::clicked, this, &ScribbleRibbon::clearClicked);

    // Sits under the dropdown and is normally empty, so the ribbon does not reserve space for
    // a line that is usually not there. It appears in place rather than as a dialog, because a
    // dialog is dismissed and forgotten.
    m_status = new QLabel;
    m_status->setVisible(false);

    auto* apiBody = new QWidget;
    auto* apiV = new QVBoxLayout(apiBody);
    apiV->setContentsMargins(0, 0, 0, 0);
    apiV->setSpacing(3);
    auto* apiRow = new QHBoxLayout;
    apiRow->setContentsMargins(0, 0, 0, 0);
    apiRow->setSpacing(6);
    apiRow->addWidget(m_api);
    apiRow->addWidget(m_clear);
    apiV->addLayout(apiRow);
    apiV->addWidget(m_status);
    row->addWidget(makeSection(QStringLiteral("PEN API"), apiBody));
    row->addWidget(separator());

    // ── BRUSH ────────────────────────────────────────────────
    m_brushLabel = makeValue(QStringLiteral("Size 6 px"));
    m_brush = new QSlider(Qt::Horizontal);
    m_brush->setRange(1, 40);
    m_brush->setValue(6);
    m_brush->setFixedWidth(130);
    connect(m_brush, &QSlider::valueChanged, this, [this](int v) {
        m_brushLabel->setText(QStringLiteral("Size %1 px").arg(v));
        Q_EMIT brushSizeChanged(double(v));
    });

    auto* brushBody = new QWidget;
    auto* brushV = new QVBoxLayout(brushBody);
    brushV->setContentsMargins(0, 0, 0, 0);
    brushV->setSpacing(3);
    brushV->addWidget(m_brushLabel);
    brushV->addWidget(m_brush);
    row->addWidget(makeSection(QStringLiteral("BRUSH"), brushBody));
    row->addWidget(separator());

    // ── PEN ──────────────────────────────────────────────────
    m_proxDot = makeDot();
    m_proxText = caption(QStringLiteral("Out"));
    m_cursor = makeValue();

    auto* penBody = new QWidget;
    auto* penG = new QGridLayout(penBody);
    penG->setContentsMargins(0, 0, 0, 0);
    penG->setHorizontalSpacing(5);
    penG->setVerticalSpacing(2);
    penG->addWidget(m_proxDot, 0, 0);
    penG->addWidget(m_proxText, 0, 1);
    penG->addWidget(caption(QStringLiteral("Cursor:")), 1, 0, 1, 1);
    penG->addWidget(m_cursor, 1, 1);
    row->addWidget(makeSection(QStringLiteral("PEN"), penBody));
    row->addWidget(separator());

    // ── BUTTONS ──────────────────────────────────────────────
    m_dotTip = makeDot(); m_dotEraser = makeDot();
    m_dotB1 = makeDot();  m_dotB2 = makeDot(); m_dotB3 = makeDot();
    m_buttonsHex = makeValue(QStringLiteral("0x00000000"));

    auto* btnBody = new QWidget;
    auto* btnG = new QGridLayout(btnBody);
    btnG->setContentsMargins(0, 0, 0, 0);
    btnG->setHorizontalSpacing(5);
    btnG->setVerticalSpacing(2);
    btnG->addWidget(m_dotTip, 0, 0);    btnG->addWidget(caption(QStringLiteral("Tip")), 0, 1);
    btnG->addWidget(m_dotEraser, 0, 2); btnG->addWidget(caption(QStringLiteral("Era")), 0, 3);
    btnG->addWidget(m_dotB1, 1, 0);     btnG->addWidget(caption(QStringLiteral("B1")), 1, 1);
    btnG->addWidget(m_dotB2, 1, 2);     btnG->addWidget(caption(QStringLiteral("B2")), 1, 3);
    btnG->addWidget(m_dotB3, 1, 4);     btnG->addWidget(caption(QStringLiteral("B3")), 1, 5);
    btnG->addWidget(m_buttonsHex, 2, 0, 1, 6);
    row->addWidget(makeSection(QStringLiteral("BUTTONS"), btnBody));
    row->addWidget(separator());

    // ── POSITION ─────────────────────────────────────────────
    m_posRaw = makeValue();
    m_posScreen = makeValue(QStringLiteral("--,--"));
    m_posApp = makeValue(QStringLiteral("--,--"));
    m_posCanvas = makeValue(QStringLiteral("--,--"));

    auto* posBody = new QWidget;
    auto* posG = new QGridLayout(posBody);
    posG->setContentsMargins(0, 0, 0, 0);
    posG->setHorizontalSpacing(5);
    posG->setVerticalSpacing(2);
    posG->addWidget(caption(QStringLiteral("Raw:")), 0, 0);    posG->addWidget(m_posRaw, 0, 1);
    posG->addWidget(caption(QStringLiteral("Screen:")), 1, 0); posG->addWidget(m_posScreen, 1, 1);
    posG->addWidget(caption(QStringLiteral("App:")), 2, 0);    posG->addWidget(m_posApp, 2, 1);
    posG->addWidget(caption(QStringLiteral("Canvas:")), 3, 0); posG->addWidget(m_posCanvas, 3, 1);
    row->addWidget(makeSection(QStringLiteral("POSITION"), posBody));
    row->addWidget(separator());

    // ── PRESSURE ─────────────────────────────────────────────
    m_prsRaw = makeValue();
    m_prsNorm = makeValue();

    auto* prsBody = new QWidget;
    auto* prsG = new QGridLayout(prsBody);
    prsG->setContentsMargins(0, 0, 0, 0);
    prsG->setHorizontalSpacing(5);
    prsG->setVerticalSpacing(2);
    prsG->addWidget(caption(QStringLiteral("Raw:")), 0, 0);  prsG->addWidget(m_prsRaw, 0, 1);
    prsG->addWidget(caption(QStringLiteral("Norm:")), 1, 0); prsG->addWidget(m_prsNorm, 1, 1);
    row->addWidget(makeSection(QStringLiteral("PRESSURE"), prsBody));
    row->addWidget(separator());

    // ── ORIENTATION ──────────────────────────────────────────
    m_oriAz = makeValue();
    m_oriAl = makeValue();
    m_oriTw = makeValue();

    auto* oriBody = new QWidget;
    auto* oriG = new QGridLayout(oriBody);
    oriG->setContentsMargins(0, 0, 0, 0);
    oriG->setHorizontalSpacing(5);
    oriG->setVerticalSpacing(2);
    oriG->addWidget(caption(QStringLiteral("Azimuth:")), 0, 0);  oriG->addWidget(m_oriAz, 0, 1);
    oriG->addWidget(caption(QStringLiteral("Altitude:")), 1, 0); oriG->addWidget(m_oriAl, 1, 1);
    oriG->addWidget(caption(QStringLiteral("Twist:")), 2, 0);    oriG->addWidget(m_oriTw, 2, 1);
    row->addWidget(makeSection(QStringLiteral("ORIENTATION"), oriBody));

    row->addStretch();
    clearReadout();
}

double ScribbleRibbon::brushSize() const {
    return double(m_brush->value());
}

void ScribbleRibbon::onApiSelected(int index) {
    const PenApi chosen = index == 1 ? PenApi::WinTab : PenApi::WmPointer;

    // Remembered for the next launch, and applied to this one. Qt 6.8.3 does switch at
    // runtime; the claim that it cannot, which this sample was built on, was wrong.
    penapi::save(chosen);
    Q_EMIT apiSelected(chosen);
}

void ScribbleRibbon::setActiveApi(PenApi obtained, bool switchSucceeded) {
    m_active = obtained;

    // Set without re-entering the handler: this is the answer to a request, not a new one.
    const QSignalBlocker block(m_api);
    m_api->setCurrentIndex(obtained == PenApi::WinTab ? 1 : 0);

    if (switchSucceeded) {
        m_status->setStyleSheet(QStringLiteral("color:#3a7a3a;"));
        m_status->setText(QStringLiteral("Now on %1.").arg(penapi::description(obtained)));
    } else {
        // The dropdown has already been moved back to what Qt reports, so this says why it
        // moved rather than leaving the user to notice.
        m_status->setStyleSheet(QStringLiteral("color:#b06000;"));
        m_status->setText(QStringLiteral("Qt refused the switch. Still on %1.")
                              .arg(penapi::description(obtained)));
    }
    m_status->setVisible(true);
}

void ScribbleRibbon::clearReadout() {
    setDot(m_proxDot, false);
    m_proxText->setText(QStringLiteral("Out"));
    m_cursor->setText(QStringLiteral("--"));

    setDot(m_dotTip, false); setDot(m_dotEraser, false);
    setDot(m_dotB1, false);  setDot(m_dotB2, false); setDot(m_dotB3, false);
    m_buttonsHex->setText(QStringLiteral("0x00000000"));

    m_posRaw->setText(QStringLiteral("--"));
    m_posScreen->setText(QStringLiteral("--,--"));
    m_posApp->setText(QStringLiteral("--,--"));
    m_posCanvas->setText(QStringLiteral("--,--"));

    m_prsRaw->setText(QStringLiteral("--"));
    m_prsNorm->setText(QStringLiteral("--"));

    m_oriAz->setText(QStringLiteral("--"));
    m_oriAl->setText(QStringLiteral("--"));
    m_oriTw->setText(QStringLiteral("--"));
}

void ScribbleRibbon::setReadout(const PenReadout& r) {
    if (!r.hasData) { clearReadout(); return; }

    setDot(m_proxDot, r.inProximity);
    m_proxText->setText(r.inProximity ? QStringLiteral("Proximity") : QStringLiteral("Out"));
    m_proxText->setStyleSheet(r.inProximity ? QStringLiteral("color:#008c00;")
                                            : QString::fromLatin1(kLabelStyle));
    m_cursor->setText(r.cursor);

    setDot(m_dotTip, r.tip);
    setDot(m_dotEraser, r.eraser, true);
    setDot(m_dotB1, r.barrel1);
    setDot(m_dotB2, r.barrel2);
    setDot(m_dotB3, r.barrel3);
    m_buttonsHex->setText(QStringLiteral("0x%1")
                              .arg(r.rawButtons, 8, 16, QLatin1Char('0')).toUpper()
                              .replace(QLatin1String("0X"), QLatin1String("0x")));

    // Qt exposes no device-native position, so this stays unavailable rather than repeating
    // Screen with its fraction removed -- which would be the first value with information
    // taken out of it, not a second measurement.
    m_posRaw->setText(QStringLiteral("--"));
    m_posScreen->setText(QStringLiteral("%1,%2")
                             .arg(r.screenX, 0, 'f', 2).arg(r.screenY, 0, 'f', 2));
    m_posApp->setText(QStringLiteral("%1,%2")
                          .arg(r.appX, 0, 'f', 2).arg(r.appY, 0, 'f', 2));
    m_posCanvas->setText(QStringLiteral("%1,%2")
                             .arg(r.canvasX, 0, 'f', 2).arg(r.canvasY, 0, 'f', 2));

    m_prsRaw->setText(QStringLiteral("%1 / %2").arg(r.rawPressure).arg(r.maxPressure));
    m_prsNorm->setText(QStringLiteral("%1").arg(r.normalisedPressure, 0, 'f', 3));

    m_oriAz->setText(QStringLiteral("%1").arg(r.azimuth, 0, 'f', 1));
    m_oriAl->setText(QStringLiteral("%1").arg(r.altitude, 0, 'f', 1));
    m_oriTw->setText(QStringLiteral("%1").arg(r.twist, 0, 'f', 1));
}
