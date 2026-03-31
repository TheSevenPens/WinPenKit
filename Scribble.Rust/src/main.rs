mod pen_session_ffi;

use eframe::egui;
use pen_session_ffi::{PenInputApi, PenPoint, PenSession};
use tiny_skia::{Color, LineCap, Paint, PathBuilder, Pixmap, Stroke, Transform};

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1200.0, 700.0])
            .with_title("Scribble Rust - PenSession"),
        ..Default::default()
    };

    eframe::run_native(
        "Scribble.Rust",
        options,
        Box::new(|_cc| Ok(Box::new(ScribbleApp::new()))),
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
    last_canvas_point: Option<(f32, f32)>,
    brush_size: f32,
    needs_texture_update: bool,

    last_point: Option<PenPoint>,
    max_pressure: i32,
    last_point_time: std::time::Instant,
}

impl ScribbleApp {
    fn new() -> Self {
        let apis = PenSession::get_available_apis();
        Self {
            available_apis: apis,
            selected_api_index: 0,
            session: None,
            points_buffer: vec![PenPoint::default(); 128],
            hwnd: std::ptr::null_mut(),
            pixmap: None,
            texture: None,
            canvas_size: [0, 0],
            last_canvas_point: None,
            brush_size: 6.0,
            needs_texture_update: false,
            last_point: None,
            max_pressure: 0,
            last_point_time: std::time::Instant::now(),
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
        self.session = Some(session);
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

            // Wintab gives physical desktop pixels. egui positions are in
            // logical points. Convert physical → logical, then subtract
            // the canvas's logical screen position.
            let canvas_x = pt.desktop_x as f32 / pixels_per_point - canvas_screen_min.x;
            let canvas_y = pt.desktop_y as f32 / pixels_per_point - canvas_screen_min.y;

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
            }
        }

        // ── Ribbon ───────────────────────────────────────────────
        egui::TopBottomPanel::top("ribbon")
            .exact_height(130.0)
            .show(ctx, |ui| {
            ui.horizontal(|ui| {

                ui.vertical(|ui| {
                    ui.strong("APP");
                    let api_names: Vec<&str> = self
                        .available_apis
                        .iter()
                        .map(|a| match a {
                            PenInputApi::WintabSystem => "Wintab",
                            PenInputApi::WintabDigitizer => "Wintab (high-res)",
                            PenInputApi::WmPointer => "WM_Pointer",
                            _ => "Unknown",
                        })
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
                    ui.strong("POSITION");
                    if let Some(pt) = &self.last_point {
                        ui.monospace(format!("Raw: {},{}", pt.raw_x, pt.raw_y));
                        ui.monospace(format!("Screen: {:.0},{:.0}", pt.desktop_x, pt.desktop_y));
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
            });
        });

        // ── Canvas ───────────────────────────────────────────────
        egui::CentralPanel::default().show(ctx, |ui| {
            let available = ui.available_size();
            self.ensure_pixmap(available.x as usize, available.y as usize);

            // egui positions are in logical points (DPI-scaled).
            // Wintab gives physical desktop pixels.
            // Convert: physical → logical by dividing by pixels_per_point,
            // then subtract the canvas's logical screen position.
            let canvas_rect = ui.min_rect();
            let canvas_screen_min = egui::pos2(
                window_pos.x + canvas_rect.min.x,
                window_pos.y + canvas_rect.min.y,
            );

            self.process_points(canvas_screen_min, ppp);
            self.update_texture(ctx);

            if let Some(ref tex) = self.texture {
                ui.image(egui::ImageSource::Texture(egui::load::SizedTexture::new(
                    tex.id(),
                    available,
                )));
            }
        });
    }
}
