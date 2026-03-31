//! FFI bindings for the pen_session C API (PenSession.Native.dll).
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
}

// Opaque handle.
pub type PenSessionHandle = *mut c_void;

#[link(name = "PenSession.Native")]
unsafe extern "C" {
    pub fn pen_session_get_available_apis(buffer: *mut PenInputApi, max_count: i32) -> i32;
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
    pub fn pen_session_get_debug_info(handle: PenSessionHandle) -> *const c_char;
    pub fn pen_session_refresh_mapping(handle: PenSessionHandle);
    pub fn pen_session_get_log_path() -> *const c_char;
}

/// Safe wrapper around the pen session handle.
pub struct PenSession {
    handle: PenSessionHandle,
}

impl PenSession {
    pub fn get_available_apis() -> Vec<PenInputApi> {
        let mut apis = [PenInputApi::WintabSystem; 8];
        let count = unsafe { pen_session_get_available_apis(apis.as_mut_ptr(), 8) };
        apis[..count as usize].to_vec()
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
