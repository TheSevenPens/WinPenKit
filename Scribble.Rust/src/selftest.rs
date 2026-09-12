//! Machine-checkable acceptance checks, and the report format they print in.
//!
//! Deliberately a reimplementation of `WinPenKit.Diagnostics.SelfTest` rather than a binding
//! to it: the point of this sample is that it has no .NET runtime under it. The check ids, the
//! line format and the exit code are identical, so a script or an agent reads this sample the
//! same way it reads the others.
//!
//! Level 0 is the environment and level 1 the drawing surface. Neither needs a tablet, a pen,
//! or a person, which is what makes them worth automating. Levels 2 and 3 - the pen stream and
//! the coordinate conversion - need input, so they are not part of a launch-time self test.

use std::ffi::c_void;

pub struct Report {
    app_name: String,
    results: Vec<(String, bool, String)>,
}

impl Report {
    pub fn new(app_name: &str) -> Self {
        Self { app_name: app_name.to_string(), results: Vec::new() }
    }

    /// `detail` is recorded whether the check passed or failed. A passing check that prints
    /// nothing is indistinguishable from a check that never ran.
    pub fn check(&mut self, id: &str, pass: bool, detail: String) {
        self.results.push((id.to_string(), pass, detail));
    }

    pub fn all_passed(&self) -> bool {
        !self.results.is_empty() && self.results.iter().all(|(_, p, _)| *p)
    }

    pub fn format(&self) -> String {
        let width = self.results.iter().map(|(id, _, _)| id.len()).max().unwrap_or(0);
        let mut out = format!("SELFTEST {}\n", self.app_name);
        let passed = self.results.iter().filter(|(_, p, _)| *p).count();
        for (id, pass, detail) in &self.results {
            out.push_str(&format!(
                "[{}] {:width$}  {}\n",
                if *pass { "PASS" } else { "FAIL" },
                id, detail, width = width
            ));
        }
        out.push_str(&format!("RESULT {}/{} passed\n", passed, self.results.len()));
        out
    }

    /// Prints the report and returns the process exit code: 0 only when everything passed.
    pub fn emit(&self) -> i32 {
        use std::io::Write;
        print!("{}", self.format());
        let _ = std::io::stdout().flush();
        if self.all_passed() { 0 } else { 1 }
    }

    // Level 0: environment

    /// Anything short of Per-Monitor V2 and Win32 hands back virtualized coordinates that do
    /// not match what the pen reports, so every later measurement is of the wrong thing.
    pub fn check_dpi_awareness(&mut self) {
        unsafe {
            let ctx = GetThreadDpiAwarenessContext();
            if ctx.is_null() {
                self.check("L0.dpi-awareness", false,
                           "could not run: GetThreadDpiAwarenessContext returned null".into());
                return;
            }
            let v2 = AreDpiAwarenessContextsEqual(ctx, DPI_PER_MONITOR_AWARE_V2) != 0;
            let name = if v2 {
                "PerMonitorV2"
            } else if AreDpiAwarenessContextsEqual(ctx, DPI_PER_MONITOR_AWARE) != 0 {
                "PerMonitor (not V2)"
            } else if AreDpiAwarenessContextsEqual(ctx, DPI_SYSTEM_AWARE) != 0 {
                "System"
            } else if AreDpiAwarenessContextsEqual(ctx, DPI_UNAWARE) != 0 {
                "Unaware"
            } else {
                "unrecognised"
            };
            self.check("L0.dpi-awareness", v2, name.into());
        }
    }

    /// Pen input is delivered by absolute screen position, so any part of the window off its
    /// monitor - or under the taskbar - receives nothing while the window keeps running and
    /// painting, which reads as a bug in whatever is being tested.
    pub fn check_window_placement(&mut self, hwnd: *mut c_void) {
        const ID: &str = "L0.window-placement";
        if hwnd.is_null() {
            self.check(ID, false, "could not run: no window handle".into());
            return;
        }
        unsafe {
            let mut client = Rect::default();
            if GetClientRect(hwnd, &mut client) == 0 {
                self.check(ID, false, "could not run: GetClientRect failed".into());
                return;
            }
            let mut origin = Point { x: 0, y: 0 };
            if ClientToScreen(hwnd, &mut origin) == 0 {
                self.check(ID, false, "could not run: ClientToScreen failed".into());
                return;
            }
            let mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            let mut mi = MonitorInfo {
                cb_size: std::mem::size_of::<MonitorInfo>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(mon, &mut mi) == 0 {
                self.check(ID, false, "could not run: GetMonitorInfo failed".into());
                return;
            }

            let w = client.right - client.left;
            let h = client.bottom - client.top;
            let wa = mi.rc_work;
            let inside = origin.x >= wa.left
                && origin.y >= wa.top
                && origin.x + w <= wa.right
                && origin.y + h <= wa.bottom;

            self.check(ID, inside, format!(
                "client {}x{} at {},{}; work area {}x{} at {},{}{}",
                w, h, origin.x, origin.y,
                wa.right - wa.left, wa.bottom - wa.top, wa.left, wa.top,
                if inside { "" } else { "  <- input outside the work area is silently discarded" }));
        }
    }

    pub fn report_scale(&mut self, scale: f32) {
        self.check("L0.scale", scale > 0.0, format!("{scale:.2}x"));
    }

    // Level 1: surface

    /// `scale` is the ratio of the layout unit to a device pixel. egui lays out in points, so
    /// here it is `pixels_per_point` - not necessarily the same thing as the display scale in
    /// other frameworks.
    pub fn check_surface_physical(&mut self, bitmap_w: u32, bitmap_h: u32,
                                  logical_w: f32, logical_h: f32, scale: f32) {
        let want_w = (logical_w * scale).ceil() as u32;
        let want_h = (logical_h * scale).ceil() as u32;
        let ok = bitmap_w == want_w && bitmap_h == want_h;

        // Only call it a resolution problem when it actually is one. A surface a pixel or
        // two short is a rounding mistake, and reporting that as "rendering at 100%" sends
        // the reader looking for the wrong bug.
        let ratio = if want_w > 0 { bitmap_w as f32 / want_w as f32 } else { 1.0 };
        let tail = if ok {
            String::new()
        } else if ratio < 0.99 {
            format!("  <- rendering at {:.0}% of display resolution", 100.0 * ratio)
        } else {
            "  <- off by a rounding step, not a scale factor".to_string()
        };

        self.check("L1.surface-physical", ok, format!(
            "bitmap {bitmap_w}x{bitmap_h}, expected {want_w}x{want_h} \
             (= ceil({logical_w:.0}x{logical_h:.0} logical x {scale:.2})){tail}"));
    }

    /// A fractional offset makes the surface get resampled to draw it between pixel rows,
    /// softening every edge at once while coordinates and resolution both still measure
    /// correct. Both axes are reported separately because the error is routinely
    /// one-dimensional.
    pub fn check_surface_alignment(&mut self, origin_x: f32, origin_y: f32) {
        let fx = (origin_x - origin_x.round()).abs();
        let fy = (origin_y - origin_y.round()).abs();
        let ok = fx < 0.01 && fy < 0.01;

        let which = if ok {
            ""
        } else if fx >= 0.01 && fy >= 0.01 {
            "  <- fractional on both axes; the whole surface is resampled to draw it"
        } else if fx >= 0.01 {
            "  <- fractional on x; the whole surface is resampled to draw it"
        } else {
            "  <- fractional on y; the whole surface is resampled to draw it"
        };

        self.check("L1.surface-alignment", ok,
                   format!("origin {origin_x:.2},{origin_y:.2}px{which}"));
    }

    /// Presenting a correctly sized surface into a differently sized rect scales it back off
    /// the pixel grid, which undoes the point of sizing it physically.
    pub fn check_presentation_1to1(&mut self, bitmap_w: u32, bitmap_h: u32,
                                   presented_w: f32, presented_h: f32) {
        let ok = (presented_w - bitmap_w as f32).abs() < 0.5
              && (presented_h - bitmap_h as f32).abs() < 0.5;

        self.check("L1.presentation-1to1", ok, format!(
            "bitmap {bitmap_w}x{bitmap_h} presented at {presented_w:.1}x{presented_h:.1} device px{}",
            if ok { "" } else { "  <- magnified or shrunk on the way to the screen" }));
    }
    // Levels 2 and 3: the replayed stroke
    //
    // A recording holds what the session produced, so replaying it exercises everything
    // downstream of the session and nothing inside it. An app that converts perfectly can
    // still be fed pre-quantized coordinates by its own session, and no replay will show
    // that; the recording-subpixel check exists so the boundary stays visible.

    /// Whether the data being replayed is sub-pixel at all. Without it, a clean result below
    /// could mean either a lossless conversion or one that had nothing left to lose.
    pub fn check_recording_subpixel(&mut self, input: &[(f64, f64)]) {
        if input.len() < 3 {
            self.check("L2.recording-subpixel", false, "could not run: recording too short".into());
            return;
        }
        let integral = input.iter()
            .filter(|(x, y)| (x - x.round()).abs() < 1e-9 && (y - y.round()).abs() < 1e-9)
            .count();
        let pct = 100.0 * integral as f64 / input.len() as f64;
        self.check("L2.recording-subpixel", pct < 5.0, format!(
            "{pct:.1}% of {} recorded points are on whole pixels{}", input.len(),
            if pct < 5.0 { "" } else { "  <- the recording is already quantized; nothing below can fail" }));
    }

    /// A legitimate pen stream essentially never lands on whole device pixels, so a high
    /// percentage here means an integer-typed API somewhere in the conversion.
    pub fn check_conversion_snap(&mut self, out: &[(f64, f64)], scale: f64) {
        if out.is_empty() {
            self.check("L3.conversion-snap", false, "could not run: no converted points".into());
            return;
        }
        let snapped = out.iter()
            .filter(|(x, y)| ((x * scale) - (x * scale).round()).abs() < 1e-6
                          && ((y * scale) - (y * scale).round()).abs() < 1e-6)
            .count();
        let pct = 100.0 * snapped as f64 / out.len() as f64;
        self.check("L3.conversion-snap", pct < 5.0, format!(
            "{pct:.1}% of converted points land on whole device pixels{}",
            if pct < 5.0 { "" } else { "  <- an integer-typed API is truncating the position" }));
    }

    /// The strongest of the three, and the only one needing no threshold. The conversion is a
    /// translation and a uniform scale, both of which preserve angles exactly, so a lossless
    /// implementation reproduces the input turn angle to the decimal. A fixed number would
    /// have to be calibrated against how the stroke was drawn; this calibrates itself.
    pub fn check_conversion_lossless(&mut self, input: &[(f64, f64)], out: &[(f64, f64)]) {
        let a = mean_turn_angle(input);
        let b = mean_turn_angle(out);
        let delta = (b - a).abs();
        let ok = delta < 0.05;
        self.check("L3.conversion-lossless", ok, format!(
            "mean turn angle in {a:.2} deg, out {b:.2} deg (delta {delta:.2}){}",
            if ok { "" } else { "  <- the conversion changed the shape of the path" }));
    }
}

/// Mean angle between consecutive segments, in degrees. Quantizing a path to a pixel grid
/// leaves only a handful of directions a short segment can point in, so it stops following the
/// pen and starts zigzagging - which shows up here and is invisible to almost everything else.
pub fn mean_turn_angle(pts: &[(f64, f64)]) -> f64 {
    let mut sum = 0.0;
    let mut n = 0;
    for i in 1..pts.len().saturating_sub(1) {
        let (ax, ay) = (pts[i].0 - pts[i - 1].0, pts[i].1 - pts[i - 1].1);
        let (bx, by) = (pts[i + 1].0 - pts[i].0, pts[i + 1].1 - pts[i].1);
        let (na, nb) = ((ax * ax + ay * ay).sqrt(), (bx * bx + by * by).sqrt());
        if na < 1e-9 || nb < 1e-9 { continue; }
        let c = ((ax * bx + ay * by) / (na * nb)).clamp(-1.0, 1.0);
        sum += c.acos().to_degrees();
        n += 1;
    }
    if n > 0 { sum / n as f64 } else { 0.0 }
}

/// Loads a recording: desktopX,desktopY,pressure, with `#` comments and a header line.
pub fn load_recording(path: &str) -> Vec<(f64, f64)> {
    let mut pts = Vec::new();
    let Ok(text) = std::fs::read_to_string(path) else { return pts };
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') { continue; }
        let mut f = line.split(',');
        let (Some(a), Some(b)) = (f.next(), f.next()) else { continue };
        if let (Ok(x), Ok(y)) = (a.trim().parse::<f64>(), b.trim().parse::<f64>()) {
            pts.push((x, y));
        }
    }
    pts
}

/// Shifts a recording so it sits inside a canvas. The shift is a whole number of pixels
/// deliberately: a fractional one would change every coordinate fractional part and so change
/// the very thing being measured.
pub fn center_on(pts: &mut [(f64, f64)], origin_x: f64, origin_y: f64, w: f64, h: f64) {
    if pts.is_empty() { return; }
    let (mut min_x, mut max_x) = (pts[0].0, pts[0].0);
    let (mut min_y, mut max_y) = (pts[0].1, pts[0].1);
    for &(x, y) in pts.iter() {
        min_x = min_x.min(x); max_x = max_x.max(x);
        min_y = min_y.min(y); max_y = max_y.max(y);
    }
    let dx = (origin_x + (w - (max_x - min_x)) / 2.0 - min_x).round();
    let dy = (origin_y + (h - (max_y - min_y)) / 2.0 - min_y).round();
    for p in pts.iter_mut() { p.0 += dx; p.1 += dy; }
}

/// Walks up from the executable looking for the bundled reference recording.
pub fn find_default_recording() -> Option<String> {
    let exe = std::env::current_exe().ok()?;
    let mut dir = exe.parent()?;
    for _ in 0..8 {
        let candidate = dir.join("testdata").join("reference-stroke.csv");
        if candidate.is_file() {
            return Some(candidate.to_string_lossy().into_owned());
        }
        dir = dir.parent()?;
    }
    None
}

/// `--replay` alone uses the bundled reference stroke; `--replay <path>` uses your own, which
/// is how a stream captured from real hardware gets checked against the same assertions.
pub fn replay_requested() -> Option<Option<String>> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    for (i, a) in args.iter().enumerate() {
        if !a.eq_ignore_ascii_case("--replay") { continue; }
        let explicit = args.get(i + 1)
            .filter(|n| !n.starts_with("--"))
            .cloned();
        return Some(explicit.or_else(find_default_recording));
    }
    None
}

/// Whether the command line asks for a self test.
pub fn requested() -> bool {
    std::env::args().skip(1).any(|a| a.eq_ignore_ascii_case("--selftest"))
}

// Win32

const MONITOR_DEFAULTTONEAREST: u32 = 2;
const DPI_UNAWARE: *mut c_void = -1isize as *mut c_void;
const DPI_SYSTEM_AWARE: *mut c_void = -2isize as *mut c_void;
const DPI_PER_MONITOR_AWARE: *mut c_void = -3isize as *mut c_void;
const DPI_PER_MONITOR_AWARE_V2: *mut c_void = -4isize as *mut c_void;

impl Report {
    /// Whether the canvas origin followed the window when the window moved.
    ///
    /// Every other check converts points while the window holds still, and a canvas origin
    /// that is simply wrong cancels out of all of them: the replay places its input relative
    /// to the origin the application reports, then the application subtracts the same value
    /// back off. `L1.surface-alignment` does not close the gap either, since it asks whether
    /// the origin is a whole number rather than whether it is the right one.
    ///
    /// This takes the two measurements rather than a conversion closure, unlike the managed
    /// and C++ versions. egui reads the window position once per frame into its input
    /// snapshot, so moving the window and re-converting inside the same frame would measure
    /// the snapshot rather than the window. The caller moves the window at the end of one
    /// frame and reports both deltas on the next.
    ///
    /// `window_delta` is how far the window moved, `origin_delta` how far the canvas origin
    /// moved with it, both in device pixels.
    pub fn check_origin_tracks_window(&mut self, window_delta: (f64, f64),
                                      origin_delta: (f64, f64)) {
        const ID: &str = "L3.origin-tracks-window";

        let err_x = (origin_delta.0 - window_delta.0).abs();
        let err_y = (origin_delta.1 - window_delta.1).abs();
        let ok = err_x < 0.5 && err_y < 0.5;

        let detail = if ok {
            format!("moved {:.0},{:.0}px; canvas origin followed",
                    window_delta.0, window_delta.1)
        } else {
            format!(
                "moved {:.0},{:.0}px; canvas origin moved {:.2},{:.2}px  <- the canvas origin is cached and nothing refreshes it when the window moves",
                window_delta.0, window_delta.1, origin_delta.0, origin_delta.1)
        };

        self.check(ID, ok, detail);
    }
}

/// The window's top-left corner in desktop pixels.
pub fn window_origin(hwnd: *mut c_void) -> Option<(i32, i32)> {
    if hwnd.is_null() { return None; }
    unsafe {
        let mut r = Rect::default();
        if GetWindowRect(hwnd, &mut r) == 0 { return None; }
        Some((r.left, r.top))
    }
}

/// Moves the window without resizing it. Returns false if the window is maximized, since
/// moving one restores it.
pub fn move_window(hwnd: *mut c_void, x: i32, y: i32) -> bool {
    if hwnd.is_null() { return false; }
    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    const MOVE_ONLY: u32 = 0x0001 | 0x0004 | 0x0010;
    unsafe {
        if IsZoomed(hwnd) != 0 { return false; }
        SetWindowPos(hwnd, std::ptr::null_mut(), x, y, 0, 0, MOVE_ONLY) != 0
    }
}

#[repr(C)]
#[derive(Default, Clone, Copy)]
struct Rect { left: i32, top: i32, right: i32, bottom: i32 }

#[repr(C)]
struct Point { x: i32, y: i32 }

#[repr(C)]
#[derive(Default)]
struct MonitorInfo { cb_size: u32, rc_monitor: Rect, rc_work: Rect, dw_flags: u32 }

unsafe extern "system" {
    fn GetThreadDpiAwarenessContext() -> *mut c_void;
    fn AreDpiAwarenessContextsEqual(a: *mut c_void, b: *mut c_void) -> i32;
    fn GetClientRect(hwnd: *mut c_void, rect: *mut Rect) -> i32;
    fn ClientToScreen(hwnd: *mut c_void, point: *mut Point) -> i32;
    fn MonitorFromWindow(hwnd: *mut c_void, flags: u32) -> *mut c_void;
    fn GetMonitorInfoW(monitor: *mut c_void, info: *mut MonitorInfo) -> i32;
    fn GetWindowRect(hwnd: *mut c_void, rect: *mut Rect) -> i32;
    fn SetWindowPos(hwnd: *mut c_void, after: *mut c_void,
                    x: i32, y: i32, cx: i32, cy: i32, flags: u32) -> i32;
    fn IsZoomed(hwnd: *mut c_void) -> i32;
}
