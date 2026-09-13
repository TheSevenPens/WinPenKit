//! FFI bindings for the pen_session C API (WinPenKit.Native.dll).
//!
//! These match pen_session.h exactly. The DLL exports both the legacy
//! wintab_session_* API and the unified pen_session_* API.

#![allow(dead_code)]

use std::ffi::c_char;
use std::ffi::c_void;

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PenInputApi {
    WintabSystem = 0,
    WintabDigitizer = 1,
    WmPointer = 2,
    WinUiPointer = 3,
    WpfStylus = 4,
    AvaloniaPointer = 5,
    WinFormsPointer = 6,
}

/// What `PenPoint::raw_x` and `raw_y` are measured in.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PenRawUnits {
    /// No device-native position; the fields are zero.
    None = 0,
    TabletNative = 1,
    ScreenPixels = 2,
    /// Hundredths of a millimetre.
    Himetric = 3,
}

/// How `PenPoint::buttons` is packed.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PenButtonEncoding {
    /// One event per packet, `(action << 16) | button_number`.
    WintabEvent = 0,
    /// A bitmask replaced every packet: bit 0 barrel, bit 1 eraser.
    PointerFlags = 1,
}

/// Where the numbers in `PenPoint::cursor` come from.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PenCursorNumbering {
    /// 13 tip, 14 eraser, written by the session.
    Normalised = 0,
    /// The driver's own number, passed through.
    DeviceAssigned = 1,
}

/// Which clock `PenPoint::timestamp_us` is counted on.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PenTimestampSource {
    /// No timestamp; the field is zero.
    #[default]
    None = 0,
    /// `QueryPerformanceCounter`, divided down to microseconds. Sub-microsecond at source.
    PerformanceCounter = 1,
    /// The millisecond counter `GetTickCount64` reads, multiplied up to microseconds.
    SystemTicks = 2,
    /// The driver's own millisecond counter -- Wintab `pkTime`. Its origin and its real
    /// granularity are not established; Wintab ignores synthetic pen input, so measuring
    /// either takes a tablet.
    DeviceTicks = 3,
}

/// What a session's points mean, for the fields whose meaning depends on the
/// backend. Read once after `start`, not inferred from the API.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct PenConventions {
    pub raw_units: PenRawUnits,
    pub buttons: PenButtonEncoding,
    pub cursor: PenCursorNumbering,
    pub timestamp: PenTimestampSource,
}

impl Default for PenConventions {
    fn default() -> Self {
        Self {
            raw_units: PenRawUnits::None,
            buttons: PenButtonEncoding::WintabEvent,
            cursor: PenCursorNumbering::DeviceAssigned,
            timestamp: PenTimestampSource::None,
        }
    }
}

impl PenTimestampSource {
    /// The name a recording header shows for this clock. Matches the managed enum's member
    /// names so one reader parses a file from any of the seven samples.
    pub fn name(self) -> &'static str {
        match self {
            PenTimestampSource::None => "None",
            PenTimestampSource::PerformanceCounter => "PerformanceCounter",
            PenTimestampSource::SystemTicks => "SystemTicks",
            PenTimestampSource::DeviceTicks => "DeviceTicks",
        }
    }
}

impl PenRawUnits {
    /// The short unit name a readout shows beside the raw pair, or an empty
    /// string when there is no value to label.
    pub fn label(self) -> &'static str {
        match self {
            PenRawUnits::TabletNative => "tablet",
            PenRawUnits::ScreenPixels => "px",
            PenRawUnits::Himetric => "0.01mm",
            PenRawUnits::None => "",
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct PenPoint {
    pub desktop_x: f64,
    pub desktop_y: f64,
    pub raw_x: i32,
    pub raw_y: i32,
    pub pressure: u32,
    pub azimuth: f64,
    pub altitude: f64,
    pub twist: f64,
    pub tilt_x: f64,
    pub tilt_y: f64,
    pub z: i32,
    pub status: u32,
    pub buttons: u32,
    pub cursor: u32,
    pub source: i32,
    /// When the point was produced, in microseconds. Subtract two of these; do not read one
    /// on its own. The origin differs per backend and none of them are comparable, so the
    /// only contract is that values from one running session increase and their difference is
    /// elapsed microseconds. `PenConventions::timestamp` names the clock.
    pub timestamp_us: i64,
}

// Opaque handle.
pub type PenSessionHandle = *mut c_void;

#[link(name = "WinPenKit.Native")]
unsafe extern "C" {
    pub fn pen_session_get_point_size() -> i32;
    pub fn pen_session_get_available_apis(buffer: *mut PenInputApi, max_count: i32) -> i32;
    pub fn pen_session_get_api_label(api: PenInputApi) -> *const c_char;
    pub fn pen_session_create(api: PenInputApi) -> PenSessionHandle;
    pub fn pen_session_create_default() -> PenSessionHandle;
    pub fn pen_session_start(handle: PenSessionHandle, app_hwnd: *mut c_void) -> *const c_char;
    pub fn pen_session_stop(handle: PenSessionHandle);
    pub fn pen_session_destroy(handle: PenSessionHandle);
    pub fn pen_session_drain_points(handle: PenSessionHandle, buffer: *mut PenPoint, max_points: i32) -> i32;
    pub fn pen_session_has_new_data(handle: PenSessionHandle) -> i32;
    pub fn pen_session_get_max_pressure(handle: PenSessionHandle) -> i32;
    pub fn pen_session_is_running(handle: PenSessionHandle) -> i32;
    pub fn pen_session_get_api(handle: PenSessionHandle) -> PenInputApi;
    pub fn pen_session_get_capabilities(handle: PenSessionHandle) -> i32;
    pub fn pen_session_get_conventions(handle: PenSessionHandle, out: *mut PenConventions);
    pub fn pen_session_get_debug_info(handle: PenSessionHandle) -> *const c_char;
    pub fn pen_session_refresh_mapping(handle: PenSessionHandle);
    pub fn pen_session_on_activated(handle: PenSessionHandle);
    pub fn pen_session_get_log_path() -> *const c_char;
}

/// Safe wrapper around the pen session handle.
pub struct PenSession {
    handle: PenSessionHandle,
}

impl PenSession {
    /// Checks that this mirror of `PenPoint` is the same size as the DLL's, returning an
    /// error message naming both when it is not.
    ///
    /// `pen_session_drain_points` memcpys an array. A size mismatch does not corrupt one
    /// field: every point after the first is read from the wrong offset, so the whole array
    /// comes back as numbers that are not positions and not pressures, with nothing to say
    /// so. That is worth one call at startup.
    ///
    /// Equal sizes do not prove equal layout. The two have only ever diverged by a field
    /// being appended, and this catches that.
    pub fn check_point_layout() -> Result<(), String> {
        let dll = unsafe { pen_session_get_point_size() } as usize;
        let ours = std::mem::size_of::<PenPoint>();
        if dll == ours {
            Ok(())
        } else {
            Err(format!(
                "PenPoint is {ours} bytes here and {dll} bytes in WinPenKit.Native.dll. \
                 The DLL beside this executable was built from a different pen_session.h; \
                 rebuild one against the other."
            ))
        }
    }

    pub fn get_available_apis() -> Vec<PenInputApi> {
        let mut apis = [PenInputApi::WintabSystem; 8];
        let count = unsafe { pen_session_get_available_apis(apis.as_mut_ptr(), 8) };
        apis[..count as usize].to_vec()
    }

    /// The short name to show for an API in a dropdown.
    ///
    /// Taken from the library rather than spelled here, so this sample cannot drift from the
    /// others over how an API is named. The returned pointer is a static ASCII string owned
    /// by the library and is never null.
    pub fn api_label(api: PenInputApi) -> &'static str {
        unsafe { std::ffi::CStr::from_ptr(pen_session_get_api_label(api)) }
            .to_str()
            .unwrap_or("Unknown")
    }

    pub fn create(api: PenInputApi) -> Option<Self> {
        let handle = unsafe { pen_session_create(api) };
        if handle.is_null() { None } else { Some(Self { handle }) }
    }

    pub fn start(&self, hwnd: *mut c_void) -> Result<(), String> {
        let err = unsafe { pen_session_start(self.handle, hwnd) };
        if err.is_null() {
            Ok(())
        } else {
            let msg = unsafe { std::ffi::CStr::from_ptr(err) };
            Err(msg.to_string_lossy().into_owned())
        }
    }

    pub fn stop(&self) {
        unsafe { pen_session_stop(self.handle) };
    }

    pub fn drain_points(&self, buffer: &mut [PenPoint]) -> usize {
        unsafe {
            pen_session_drain_points(self.handle, buffer.as_mut_ptr(), buffer.len() as i32) as usize
        }
    }

    pub fn max_pressure(&self) -> i32 {
        unsafe { pen_session_get_max_pressure(self.handle) }
    }

    pub fn is_running(&self) -> bool {
        unsafe { pen_session_is_running(self.handle) != 0 }
    }

    pub fn api(&self) -> PenInputApi {
        unsafe { pen_session_get_api(self.handle) }
    }

    /// What this session's points mean. Call after `start`: a digitizer whose
    /// hi-res context failed reports screen pixels, and that is not known until
    /// the context has been opened.
    pub fn conventions(&self) -> PenConventions {
        let mut c = PenConventions::default();
        unsafe { pen_session_get_conventions(self.handle, &mut c) };
        c
    }

    /// Tell the session the window has just been activated.
    ///
    /// Wintab delivers packets to whichever context is on top of the driver's
    /// overlap order, and losing focus to another application drops ours down it
    /// with nothing to put it back - so the first stroke after returning is
    /// silently swallowed. A no-op for the pointer backends.
    pub fn on_activated(&self) {
        unsafe { pen_session_on_activated(self.handle) };
    }
}

impl Drop for PenSession {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe {
                pen_session_stop(self.handle);
                pen_session_destroy(self.handle);
            }
            self.handle = std::ptr::null_mut();
        }
    }
}
