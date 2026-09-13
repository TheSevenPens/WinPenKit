#pragma once

#include <QImage>
#include <QLabel>
#include <QMainWindow>
#include <QPointF>
#include <QWidget>

#include <array>
#include <optional>
#include <vector>

// The pen backend Qt was asked to start with. Qt settles this during platform plugin
// initialisation, before QApplication is running, so it cannot change while the process lives
// and the application has no way to ask afterwards which one it got. All this sample can
// honestly report is what it requested, which is also all Krita can report.
enum class PenBackend {
    WmPointer,   // Qt's default on Windows
    WinTab,      // selected with -platform windows:nowmpointer
};

/// The drawing surface. A QImage in physical pixels, presented at a device pixel ratio so one
/// image pixel covers one device pixel, and a QTabletEvent handler that never falls back to
/// the mouse.
class CanvasWidget : public QWidget {
    Q_OBJECT

public:
    explicit CanvasWidget(QWidget* parent = nullptr);

    /// Physical pixel size of the backing image, which is not the widget's logical size.
    QSize surfaceSize() const { return m_image.size(); }

    /// The surface's top-left corner in physical desktop pixels, kept fractional. The self
    /// test asks whether this lands on a whole pixel; rounding it here would answer yes by
    /// construction.
    QPointF surfaceOriginPx() const;

    /// Physical desktop pixels to surface pixels. The conversion the pen goes through, exposed
    /// so the replay and the origin-tracking check exercise it rather than a copy of it.
    QPointF desktopToCanvas(double x, double y) const;

    double brushWidth() const { return m_brushWidth; }
    void setBrushWidth(double w) { m_brushWidth = w; }

    void clear();

    /// Draws a filled square in surface pixels. Used by the presentation probe, which needs
    /// marks at coordinates it chose rather than anything the pen produced.
    void fillSurfaceRect(int x, int y, int size, int r, int g, int b);

    /// Starts capturing every point to `path`, written when the window closes.
    void recordTo(const std::string& path) { m_recordPath = path; }
    const std::string& recordPath() const { return m_recordPath; }

    /// The captured stream, in physical desktop pixels with pressure on a 0..1024 scale.
    const std::vector<std::array<double, 3>>& recorded() const { return m_recorded; }

signals:
    void telemetryChanged(const QString& text);

protected:
    void tabletEvent(QTabletEvent* event) override;
    void paintEvent(QPaintEvent* event) override;
    void resizeEvent(QResizeEvent* event) override;

private:
    void ensureImage();
    void drawSegment(const QPointF& fromPx, const QPointF& toPx, double pressure);

    QImage m_image;
    std::optional<QPointF> m_lastCanvasPx;
    // Contact is tracked from TabletPress and TabletRelease rather than inferred from
    // pressure. Qt's release event carries a default pressure of 0.5, not 0, so a pressure
    // test records one spurious point per stroke at the wrong pressure -- measured, not
    // assumed: a 62-point injected stroke came out 61 points at 700 and one at 512.
    bool m_inContact = false;
    double m_brushWidth = 6.0;

    std::string m_recordPath;
    std::vector<std::array<double, 3>> m_recorded;
};

/// A readout above a canvas. Deliberately the same fields the other six samples show, because
/// a number that cannot be compared against theirs is not worth displaying here.
class ScribbleWindow : public QMainWindow {
    Q_OBJECT

public:
    explicit ScribbleWindow(PenBackend backend, QWidget* parent = nullptr);

    CanvasWidget* canvas() const { return m_canvas; }
    PenBackend backend() const { return m_backend; }

    /// Runs the launch-time acceptance checks and returns the process exit code.
    int runSelfTest(const std::string& replayPath);

    void saveRecording();

private:
    PenBackend m_backend;
    CanvasWidget* m_canvas = nullptr;
    QLabel* m_backendLabel = nullptr;
    QLabel* m_telemetry = nullptr;
};
