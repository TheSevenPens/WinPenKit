mod pen_session_ffi;
mod selftest;

use eframe::egui;
use pen_session_ffi::{
    PenButtonEncoding, PenConventions, PenInputApi, PenPoint, PenRawUnits, PenSession,
};
use tiny_skia::{Color, LineCap, Paint, PathBuilder, Pixmap, Stroke, Transform};

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            // Points, so this is multiplied by the display scale. At 2.25x, 700 points is
            // 1575px before chrome, which does not fit a 2052px work area once the window
            // manager has cascaded it down a few launches - and a window whose bottom is
            // under the taskbar silently discards every pen point aimed there.
            .with_inner_size([1100.0, 600.0])
            .with_title("Scribble Rust - WinPenKit"),
        ..Default::default()
    };

    let replay = selftest::replay_requested();
    let run_selftest = selftest::requested() || replay.is_some();

    eframe::run_native(
        "Scribble.Rust",
        options,
        Box::new(move |_cc| {
            let mut app = ScribbleApp::new();
            app.selftest_pending = run_selftest;
            app.replay_path = replay.clone().flatten();
            app.replay_requested = replay.is_some();
            Ok(Box::new(app))
        }),
    )
}

struct ScribbleApp {
    available_apis: Vec<PenInputApi>,
    selected_api_index: usize,
    session: Option<PenSession>,
    points_buffer: Vec<PenPoint>,
    hwnd: *mut std::ffi::c_void,

    pixmap: Option<Pixmap>,
    texture: Option<egui::TextureHandle>,
    canvas_size: [usize; 2],

    /// Set from the command line. The checks need a canvas that has been laid out, and the
    /// earliest that exists is inside the first update() that sizes the pixmap.
    selftest_pending: bool,
    selftest_frames: u32,
    replay_requested: bool,
    // Set on the frame that nudges the window, read on the next one. egui reads the window
    // position once per frame, so the move and the measurement cannot share a frame.
    origin_probe: Option<((i32, i32), (f32, f32))>,
    replay_path: Option<String>,
    last_canvas_point: Option<(f32, f32)>,
    brush_size: f32,
    needs_texture_update: bool,

    last_point: Option<PenPoint>,
    max_pressure: i32,
    last_point_time: std::time::Instant,

    // What this session's points mean. Read once at start rather than inferred
    // per packet from the API: two implementations of one API can disagree about
    // the encoding, which is what issue 40 was.
    conventions: PenConventions,

    // Button state tracked from Wintab packet stream (relative encoding).
    tip_down: bool,
    barrel1_down: bool,
    barrel2_down: bool,
    barrel3_down: bool,
    last_raw_buttons: u32,

    // Focus tracking. egui has no activation event, so the false -> true edge of
    // the viewport's focus flag stands in for one.
    was_focused: bool,
}

impl ScribbleApp {
    fn new() -> Self {
        let apis = PenSession::get_available_apis();
        Self {
            available_apis: apis,
            selected_api_index: 0,
            session: None,
            conventions: PenConventions::default(),
            points_buffer: vec![PenPoint::default(); 128],
            hwnd: std::ptr::null_mut(),
            pixmap: None,
            texture: None,
            canvas_size: [0, 0],
            selftest_pending: false,
            selftest_frames: 0,
            replay_requested: false,
            origin_probe: None,
            replay_path: None,
            last_canvas_point: None,
            brush_size: 6.0,
            needs_texture_update: false,
            last_point: None,
            max_pressure: 0,
            last_point_time: std::time::Instant::now(),
            tip_down: false,
            barrel1_down: false,
            barrel2_down: false,
            barrel3_down: false,
            last_raw_buttons: 0,
            // Starts true so a window that opens focused does not report an edge on
            // its first frame, before there is even a session to notify.
            was_focused: true,
        }
    }

    fn start_session(&mut self) {
        self.session = None;
        self.last_canvas_point = None;

        if self.selected_api_index >= self.available_apis.len() {
            return;
        }

        let api = self.available_apis[self.selected_api_index];
        let session = match PenSession::create(api) {
            Some(s) => s,
            None => return,
        };

        if let Err(e) = session.start(self.hwnd) {
            eprintln!("Start failed: {e}");
            return;
        }

        self.max_pressure = session.max_pressure();
        self.conventions = session.conventions();
        self.session = Some(session);
        self.tip_down = false;
        self.barrel1_down = false;
        self.barrel2_down = false;
        self.barrel3_down = false;
        self.last_raw_buttons = 0;
    }

    fn ensure_pixmap(&mut self, width: usize, height: usize) {
        if width == 0 || height == 0 {
            return;
        }
        if self.canvas_size == [width, height] && self.pixmap.is_some() {
            return;
        }

        let mut pixmap = Pixmap::new(width as u32, height as u32).unwrap();
        pixmap.fill(Color::from_rgba8(0xF0, 0xF0, 0xF0, 0xFF));

        self.pixmap = Some(pixmap);
        self.canvas_size = [width, height];
        self.needs_texture_update = true;
    }

    fn clear_pixmap(&mut self) {
        if let Some(pixmap) = &mut self.pixmap {
            pixmap.fill(Color::from_rgba8(0xF0, 0xF0, 0xF0, 0xFF));
            self.needs_texture_update = true;
        }
    }

    fn process_points(&mut self, canvas_screen_min: egui::Pos2, pixels_per_point: f32) {
        let Some(session) = &self.session else { return };

        let count = session.drain_points(&mut self.points_buffer);
        if count == 0 {
            return;
        }

        let Some(pixmap) = &mut self.pixmap else { return };

        let max_p = self.max_pressure as f32;
        let mut drew = false;

        for i in 0..count {
            let pt = self.points_buffer[i];

            // Wintab and pointer-style backends use different button encodings.
            // Wintab: one event per packet, (action << 16) | buttonNumber.
            // Pointer: absolute bitmask (0x0001 = barrel, 0x0002 = eraser).
            // Pointer APIs only expose a single barrel — B2/B3 cannot light up.
            if self.conventions.buttons == PenButtonEncoding::WintabEvent {
                let btn_action = (pt.buttons >> 16) & 0xFFFF;
                let btn_number = pt.buttons & 0xFFFF;
                match btn_action {
                    2 => match btn_number {
                        0 => self.tip_down = true,
                        1 => self.barrel1_down = true,
                        2 => self.barrel2_down = true,
                        3 => self.barrel3_down = true,
                        _ => {}
                    },
                    1 => match btn_number {
                        0 => self.tip_down = false,
                        1 => self.barrel1_down = false,
                        2 => self.barrel2_down = false,
                        3 => self.barrel3_down = false,
                        _ => {}
                    },
                    _ => {}
                }
            } else {
                self.barrel1_down = (pt.buttons & 0x0001) != 0;
                self.barrel2_down = false;
                self.barrel3_down = false;
                // tip is derived from pressure below.
            }
            if pt.buttons != 0 {
                self.last_raw_buttons = pt.buttons;
            }

            // Wintab gives physical desktop pixels, and the pixmap is in physical pixels too.
            // Both terms in physical pixels: the pen position already is, and the canvas
            // origin is in points so it multiplies up. Dividing the pen position down instead
            // would throw away the sub-pixel precision the digitizer context exists to provide.
            let canvas_x = pt.desktop_x as f32 - canvas_screen_min.x * pixels_per_point;
            let canvas_y = pt.desktop_y as f32 - canvas_screen_min.y * pixels_per_point;

            if canvas_x < 0.0
                || canvas_y < 0.0
                || canvas_x > self.canvas_size[0] as f32
                || canvas_y > self.canvas_size[1] as f32
            {
                self.last_canvas_point = None;
                continue;
            }

            if let Some((from_x, from_y)) = self.last_canvas_point {
                if pt.pressure > 0 && max_p > 0.0 {
                    // Brush size is a count of physical pixels, the same as in every other
                    // sample. The pixmap is physical and so are these coordinates, so the
                    // width needs no scaling - applying it is what made this slider mean
                    // something different from the one in Scribble.Win32.
                    let width = (pt.pressure as f32 / max_p) * self.brush_size + 0.5;

                    let mut paint = Paint::default();
                    paint.set_color_rgba8(0, 0, 0, 255);
                    paint.anti_alias = true;

                    let stroke = Stroke {
                        width,
                        line_cap: LineCap::Round,
                        ..Default::default()
                    };

                    let mut pb = PathBuilder::new();
                    pb.move_to(from_x, from_y);
                    pb.line_to(canvas_x, canvas_y);
                    if let Some(path) = pb.finish() {
                        pixmap.stroke_path(&path, &paint, &stroke, Transform::identity(), None);
                        drew = true;
                    }
                }
            }

            self.last_canvas_point = Some((canvas_x, canvas_y));
            self.last_point = Some(pt);
        }

        if drew {
            self.needs_texture_update = true;
        }

        self.last_point_time = std::time::Instant::now();
    }

    fn update_texture(&mut self, ctx: &egui::Context) {
        if !self.needs_texture_update {
            return;
        }
        self.needs_texture_update = false;

        let Some(pixmap) = &self.pixmap else { return };

        let image = egui::ColorImage::from_rgba_unmultiplied(
            [pixmap.width() as usize, pixmap.height() as usize],
            pixmap.data(),
        );

        match &mut self.texture {
            Some(tex) => tex.set(image, egui::TextureOptions::NEAREST),
            None => {
                self.texture =
                    Some(ctx.load_texture("canvas", image, egui::TextureOptions::NEAREST));
            }
        }
    }
}

impl eframe::App for ScribbleApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        ctx.request_repaint();

        // Wintab drops our context down the driver's overlap order when another
        // application takes focus, and nothing puts it back - the first stroke after
        // returning is silently swallowed. The other samples hook a window activation
        // event; egui exposes focus as state rather than an event, so watch its edge.
        let focused = ctx.input(|i| i.viewport().focused.unwrap_or(true));
        if focused && !self.was_focused {
            if let Some(session) = &self.session {
                session.on_activated();
            }
        }
        self.was_focused = focused;

        let ppp = ctx.pixels_per_point();
        let window_pos = ctx.input(|i| {
            i.viewport()
                .inner_rect
                .map(|r| r.min)
                .unwrap_or_default()
        });

        // Grab the HWND on first frame (needed for WM_POINTER sessions).
        // GetActiveWindow is a pragmatic shortcut; the raw-window-handle
        // crate would be the proper approach but adds dependency overhead.
        if self.hwnd.is_null() {
            #[link(name = "user32")]
            unsafe extern "system" {
                fn GetActiveWindow() -> *mut std::ffi::c_void;
            }
            let hwnd = unsafe { GetActiveWindow() };
            if !hwnd.is_null() {
                self.hwnd = hwnd;
                // Once, as soon as the window exists. eframe sizes in points and the window
                // manager cascades, so a 600-point window on a 2.25x display can start with
                // its lower edge under the taskbar - where pen input is discarded silently.
                selftest::clamp_to_work_area(hwnd);
            }
        }

        // ── Ribbon ───────────────────────────────────────────────
        egui::TopBottomPanel::top("ribbon")
            .exact_height(130.0)
            .show(ctx, |ui| {
            ui.horizontal(|ui| {

                ui.vertical(|ui| {
                    ui.strong("PEN API");
                    let api_names: Vec<&str> = self
                        .available_apis
                        .iter()
                        .map(|a| PenSession::api_label(*a))
                        .collect();

                    let old = self.selected_api_index;
                    egui::ComboBox::from_id_salt("api")
                        .selected_text(
                            api_names
                                .get(self.selected_api_index)
                                .copied()
                                .unwrap_or("--"),
                        )
                        .show_ui(ui, |ui| {
                            for (i, name) in api_names.iter().enumerate() {
                                ui.selectable_value(&mut self.selected_api_index, i, *name);
                            }
                        });

                    if self.selected_api_index != old || self.session.is_none() {
                        self.start_session();
                    }

                    if ui.button("Clear").clicked() {
                        self.clear_pixmap();
                        self.last_canvas_point = None;
                    }
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("BRUSH");
                    ui.label(format!("Size {} px", self.brush_size as i32));
                    ui.add(
                        egui::Slider::new(&mut self.brush_size, 1.0..=50.0).show_value(false),
                    );
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("PEN");
                    let in_prox =
                        self.last_point_time.elapsed() < std::time::Duration::from_millis(200);
                    let dot = if in_prox { "🟢" } else { "⚫" };
                    ui.label(format!(
                        "{dot} {}",
                        if in_prox { "Proximity" } else { "Out" }
                    ));
                    if let Some(pt) = &self.last_point {
                        ui.label(format!("Cursor: {}", pt.cursor));
                    }
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("BUTTONS");
                    let is_eraser = self
                        .last_point
                        .as_ref()
                        .map(|p| p.cursor == 14)
                        .unwrap_or(false);
                    let pressure = self.last_point.as_ref().map(|p| p.pressure).unwrap_or(0);
                    let tip_active = (self.tip_down || pressure > 0) && !is_eraser;

                    let dot = |on: bool, eraser: bool| -> &'static str {
                        if !on {
                            "⚫"
                        } else if eraser {
                            "🟠"
                        } else {
                            "🟢"
                        }
                    };

                    ui.horizontal(|ui| {
                        ui.label(format!("{} Tip", dot(tip_active, false)));
                        ui.label(format!("{} Era", dot(is_eraser, true)));
                    });
                    ui.horizontal(|ui| {
                        ui.label(format!("{} B1", dot(self.barrel1_down, false)));
                        ui.label(format!("{} B2", dot(self.barrel2_down, false)));
                        ui.label(format!("{} B3", dot(self.barrel3_down, false)));
                    });
                    ui.monospace(format!("0x{:08X}", self.last_raw_buttons));
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("POSITION");
                    if let Some(pt) = &self.last_point {
                        // The unit travels with the reading: raw_x means a different
                        // quantity on each backend, and on some it means nothing at all.
                        ui.monospace(if self.conventions.raw_units == PenRawUnits::None {
                            "Raw: --".to_string()
                        } else {
                            format!(
                                "Raw: {},{} ({})",
                                pt.raw_x,
                                pt.raw_y,
                                self.conventions.raw_units.label()
                            )
                        });
                        // A pen position is sub-pixel, so this is shown to two decimals. At zero decimals the readout cannot show the one fault it would most often be used to find: a coordinate quantized to a whole pixel looks identical to a good one.
                        ui.monospace(format!("Screen: {:.2},{:.2}", pt.desktop_x, pt.desktop_y));
                        let app_x = pt.desktop_x as f32 / ppp - window_pos.x;
                        let app_y = pt.desktop_y as f32 / ppp - window_pos.y;
                        ui.monospace(format!("App: {app_x:.0},{app_y:.0}"));
                        if let Some((cx, cy)) = self.last_canvas_point {
                            ui.monospace(format!("Canvas: {cx:.1},{cy:.1}"));
                        }
                    }
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("PRESSURE");
                    if let Some(pt) = &self.last_point {
                        let pct = if self.max_pressure > 0 {
                            pt.pressure as f32 / self.max_pressure as f32 * 100.0
                        } else {
                            0.0
                        };
                        ui.monospace(format!("Raw: {}", pt.pressure));
                        ui.monospace(format!("Norm: {pct:.1}%"));
                    }
                });
                ui.separator();

                ui.vertical(|ui| {
                    ui.strong("ORIENTATION");
                    if let Some(pt) = &self.last_point {
                        ui.monospace(format!("Azi: {:.1}", pt.azimuth));
                        ui.monospace(format!("Alt: {:.1}", pt.altitude));
                        ui.monospace(format!("Twist: {:.1}", pt.twist));
                    }
                });
                ui.separator();

                // The canvas is only as good as the pixmap behind it: if this is not the
                // window's physical pixel size, the texture is being magnified and no amount
                // of coordinate precision will make the strokes look right.
                ui.vertical(|ui| {
                    ui.strong("SURFACE");
                    ui.monospace(format!(
                        "Pixmap: {}x{}",
                        self.canvas_size[0], self.canvas_size[1]
                    ));
                    ui.monospace(format!("Scale: {ppp:.2}x"));
                });
            });
        });

        // ── Canvas ───────────────────────────────────────────────
        egui::CentralPanel::default().show(ctx, |ui| {
            let available = ui.available_size();

            // The pixmap is sized in PHYSICAL PIXELS, not egui points. available_size() is in
            // points, and the texture is displayed at that same point size - so a point-sized
            // pixmap is one texel per point, which on a 1.75x display egui magnifies by 1.75
            // with TextureOptions::NEAREST, i.e. no filtering at all. That is a canvas drawn at
            // 57% of the screen's resolution and then blown up with hard edges, and no amount
            // of coordinate precision survives it.
            // ceil, not truncate: a pixmap one pixel short of the canvas leaves a strip the
            // stroke can never reach, and puts the surface permanently out of step with the
            // rect it is presented into.
            self.ensure_pixmap(
                (available.x * ppp).ceil() as usize,
                (available.y * ppp).ceil() as usize,
            );

            // Drawing therefore happens in physical pixels too. egui positions are in points,
            // and Wintab gives physical desktop pixels, so the canvas origin is the thing that
            // gets converted - by multiplying up - rather than the pen position being divided
            // down.
            let canvas_rect = ui.min_rect();

            // Snap the canvas to whole device pixels. egui has no equivalent of layout
            // rounding, so a panel below a text-sized ribbon starts wherever that ribbon
            // happens to end - routinely half a pixel off. The texture is then resampled
            // across the whole canvas to draw it there, softening every edge at once while
            // the coordinates and the resolution both still measure correct.
            let raw_screen_min = egui::pos2(
                window_pos.x + canvas_rect.min.x,
                window_pos.y + canvas_rect.min.y,
            );
            let canvas_screen_min = egui::pos2(
                (raw_screen_min.x * ppp).round() / ppp,
                (raw_screen_min.y * ppp).round() / ppp,
            );
            let snap_shift = canvas_screen_min - raw_screen_min;

            self.process_points(canvas_screen_min, ppp);
            self.update_texture(ctx);

            if let Some(ref tex) = self.texture {
                // Presented at exactly the pixmap's size, at the snapped origin, so one texel
                // covers one device pixel. Drawing it at `available` instead would stretch a
                // ceil-rounded pixmap by a fraction of a pixel and undo the snapping.
                let size_pt = egui::vec2(
                    self.canvas_size[0] as f32 / ppp,
                    self.canvas_size[1] as f32 / ppp,
                );
                let rect = egui::Rect::from_min_size(canvas_rect.min + snap_shift, size_pt);
                egui::Image::new(egui::load::SizedTexture::new(tex.id(), size_pt))
                    .paint_at(ui, rect);
            }

            // The handle is captured from GetActiveWindow, which returns null until the
            // window has focus, so the checks wait for it - with a frame budget so a window
            // that never activates still produces a report rather than hanging.
            self.selftest_frames += 1;
            if self.selftest_pending
                && self.canvas_size[0] > 0
                && (!self.hwnd.is_null() || self.selftest_frames > 60)
            {
                // One frame before the checks: nudge the window, and come back next frame to
                // see whether the canvas origin moved with it. Odd numbers, so a conversion
                // that happens to quantize cannot match by luck.
                const DX: i32 = 37;
                const DY: i32 = 23;
                if self.origin_probe.is_none() {
                    if let Some((wx, wy)) = selftest::window_origin(self.hwnd) {
                        if selftest::move_window(self.hwnd, wx + DX, wy + DY) {
                            self.origin_probe =
                                Some(((wx, wy), (canvas_screen_min.x, canvas_screen_min.y)));
                            ctx.request_repaint();
                            return;
                        }
                    }
                }

                self.selftest_pending = false;

                let mut r = selftest::Report::new("Scribble.Rust");
                r.check_dpi_awareness();
                r.check_window_placement(self.hwnd);
                r.report_scale(ppp);

                // egui lays out in points, so the ratio here is pixels_per_point.
                r.check_surface_physical(
                    self.canvas_size[0] as u32, self.canvas_size[1] as u32,
                    available.x, available.y, ppp);

                // The canvas rect is in points; scaled up it is where the surface actually
                // lands, which is the thing that has to be whole.
                r.check_surface_alignment(canvas_screen_min.x * ppp, canvas_screen_min.y * ppp);

                // Drawn into a rect of `available` points, which is the pixmap's size in
                // points when the surface is correct.
                r.check_presentation_1to1(
                    self.canvas_size[0] as u32, self.canvas_size[1] as u32,
                    self.canvas_size[0] as f32, self.canvas_size[1] as f32);

                if self.replay_requested {
                    match &self.replay_path {
                        None => r.check("L2.recording-subpixel", false,
                                        "could not run: reference recording not found".into()),
                        Some(path) => {
                            let mut input = selftest::load_recording(path);
                            if input.is_empty() {
                                r.check("L2.recording-subpixel", false,
                                        "could not run: recording empty or unreadable".into());
                            } else {
                                let ox = canvas_screen_min.x as f64 * ppp as f64;
                                let oy = canvas_screen_min.y as f64 * ppp as f64;
                                selftest::center_on(&mut input, ox, oy,
                                                    self.canvas_size[0] as f64,
                                                    self.canvas_size[1] as f64);

                                // Through the same arithmetic the pen goes through. A replay
                                // with its own conversion would be testing itself.
                                let out: Vec<(f64, f64)> = input.iter()
                                    .map(|&(x, y)| (x - ox, y - oy))
                                    .collect();

                                r.check_recording_subpixel(&input);
                                r.check_conversion_snap(&out, 1.0);
                                r.check_conversion_lossless(&input, &out);
                            }
                        }
                    }
                }

                // Last: the window was nudged a frame ago, so this reads what moved with it.
                match self.origin_probe.take() {
                    Some(((wx, wy), (ox, oy))) => {
                        let now = selftest::window_origin(self.hwnd).unwrap_or((wx, wy));
                        let window_delta = ((now.0 - wx) as f64, (now.1 - wy) as f64);
                        let origin_delta = (((canvas_screen_min.x - ox) * ppp) as f64,
                                            ((canvas_screen_min.y - oy) * ppp) as f64);
                        selftest::move_window(self.hwnd, wx, wy);
                        r.check_origin_tracks_window(window_delta, origin_delta);
                    }
                    None => r.check("L3.origin-tracks-window", false,
                                    "could not run: window would not move".into()),
                }

                std::process::exit(r.emit());
            }
        });
    }
}
