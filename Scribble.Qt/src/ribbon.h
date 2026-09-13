#pragma once

#include <QComboBox>
#include <QLabel>
#include <QPushButton>
#include <QSlider>
#include <QWidget>

#include "penapi.h"

/// What the ribbon shows about the most recent pen point.
///
/// Assembled by the canvas and handed over whole, so the ribbon holds no pen state of its own
/// and there is one place that decides what each field means.
struct PenReadout {
    bool hasData = false;
    bool inProximity = false;

    double screenX = 0, screenY = 0;   ///< Physical desktop pixels.
    double appX = 0, appY = 0;         ///< Physical pixels from the window's client origin.
    double canvasX = 0, canvasY = 0;   ///< Physical pixels from the canvas origin.

    int rawPressure = 0;               ///< 0..maxPressure.
    int maxPressure = 1024;
    double normalisedPressure = 0;     ///< What Qt actually reports, 0..1.

    double azimuth = 0, altitude = 0, twist = 0;
    double tiltX = 0, tiltY = 0;

    bool tip = false, eraser = false, barrel1 = false, barrel2 = false, barrel3 = false;
    unsigned rawButtons = 0;

    QString cursor = QStringLiteral("--");
};

/// The standard Scribble ribbon: the same sections, in the same order, as the other six
/// samples, so a reading here sits beside theirs without translation.
///
/// Three fields differ, and differ because Qt cannot answer them rather than because this
/// sample chose not to. They are shown as unavailable rather than filled with a stand-in:
///
///  * Raw position. Qt exposes no device-native coordinate, only the mapped QPointF. The other
///    samples report Wintab units, screen pixels or hundredths of a millimetre here.
///  * Cursor. Qt reports a pointer type, not the driver's cursor number, so this shows Pen or
///    Eraser where the others show a number.
///  * Pressure Raw. Qt normalises to 0..1, so the raw column is reconstructed against an
///    assumed maximum of 1024 and the Norm column is the value Qt actually gave.
class ScribbleRibbon : public QWidget {
    Q_OBJECT

public:
    /// `active` is the API this process is really running on, which is what the dropdown shows
    /// until the user changes it.
    explicit ScribbleRibbon(PenApi active, QWidget* parent = nullptr);

    void setReadout(const PenReadout& r);
    void clearReadout();

    double brushSize() const;

    /// Called by the window once it knows what the switch actually produced, so the ribbon
    /// never shows a backend on the strength of a request alone.
    void setActiveApi(PenApi obtained, bool switchSucceeded);

signals:
    void clearClicked();
    void brushSizeChanged(double px);

    /// The user picked one. The window performs the switch; the ribbon does not.
    void apiSelected(PenApi api);

private slots:
    void onApiSelected(int index);

private:
    QWidget* makeSection(const QString& header, QWidget* body);
    QLabel* makeValue(const QString& initial = QStringLiteral("--"));
    QLabel* makeDot();
    static void setDot(QLabel* dot, bool on, bool eraserColour = false);

    PenApi m_active;

    QComboBox* m_api = nullptr;
    QLabel* m_status = nullptr;
    QPushButton* m_clear = nullptr;

    QSlider* m_brush = nullptr;
    QLabel* m_brushLabel = nullptr;

    QLabel* m_proxDot = nullptr;
    QLabel* m_proxText = nullptr;
    QLabel* m_cursor = nullptr;

    QLabel* m_dotTip = nullptr;
    QLabel* m_dotEraser = nullptr;
    QLabel* m_dotB1 = nullptr;
    QLabel* m_dotB2 = nullptr;
    QLabel* m_dotB3 = nullptr;
    QLabel* m_buttonsHex = nullptr;

    QLabel* m_posRaw = nullptr;
    QLabel* m_posScreen = nullptr;
    QLabel* m_posApp = nullptr;
    QLabel* m_posCanvas = nullptr;

    QLabel* m_prsRaw = nullptr;
    QLabel* m_prsNorm = nullptr;

    QLabel* m_oriAz = nullptr;
    QLabel* m_oriAl = nullptr;
    QLabel* m_oriTw = nullptr;
};
