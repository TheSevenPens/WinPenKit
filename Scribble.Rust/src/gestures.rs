//! Which Windows system gestures and pen feedback this window opts out of.
//!
//! Windows asks a window with `WM_TABLET_QUERYSYSTEMGESTURESTATUS` which system gestures it
//! wants, and the window answers with a mask of the ones to switch off. By default a window
//! receives all of them. A reimplementation of `WinPenKit.TabletGestures` rather than a binding
//! to it, for the same reason the rest of this sample's self test is: there is no .NET runtime
//! under it. The values, the environment variable and the check id are the same.
//!
//! A drawing surface wants none of three things: the press-and-hold gesture (a held pen becomes
//! a right click, after a ring), the circle Windows draws when a pen button goes down, and the
//! wave it draws on pen up. Values and descriptions are from Microsoft's page for the message
//! (`tpcshrd.h`): `TABLET_DISABLE_PRESSANDHOLD` 0x1, `TABLET_DISABLE_PENTAPFEEDBACK` 0x8 ("UI
//! feedback on pen up (waves)") and `TABLET_DISABLE_PENBARRELFEEDBACK` 0x10 ("UI feedback on pen
//! button down (circle)").
//!
//! The answer can be overridden from the environment (`WINPENKIT_TABLET_GESTURE_FLAGS`,
//! hexadecimal) so each flag can be tried alone; `0` answers "disable nothing", Windows'
//! default and the baseline for measuring what Windows does. The number of times Windows has
//! asked is kept, because whether the window is asked at all is the first thing to know when a
//! ring does not go away.

use std::ffi::c_void;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicI64, Ordering};

/// `WM_TABLET_QUERYSYSTEMGESTURESTATUS` = `WM_TABLET_DEFBASE` (0x02C0) + 12.
pub const QUERY_MESSAGE: u32 = 0x02CC;

pub const DISABLE_PRESS_AND_HOLD: u32 = 0x0000_0001;
pub const DISABLE_PEN_TAP_FEEDBACK: u32 = 0x0000_0008;
pub const DISABLE_PEN_BARREL_FEEDBACK: u32 = 0x0000_0010;

/// What the window answers unless overridden.
pub const DEFAULT: u32 = DISABLE_PRESS_AND_HOLD | DISABLE_PEN_TAP_FEEDBACK | DISABLE_PEN_BARREL_FEEDBACK;

pub const OVERRIDE_VARIABLE: &str = "WINPENKIT_TABLET_GESTURE_FLAGS";

static QUERIES: AtomicI64 = AtomicI64::new(0);

/// Hexadecimal text (an optional `0x` first) to a mask. Anything that is not hexadecimal, or is
/// blank, gives `fallback`, so a typo never silently switches the flags off.
pub fn parse_override(text: Option<&str>, fallback: u32) -> u32 {
    let Some(text) = text else { return fallback };
    let text = text.trim();
    let digits = text
        .strip_prefix("0x")
        .or_else(|| text.strip_prefix("0X"))
        .unwrap_or(text);
    if digits.is_empty() || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return fallback;
    }
    u32::from_str_radix(digits, 16).unwrap_or(fallback)
}

/// The mask the window answers with.
pub fn flags() -> u32 {
    static FLAGS: OnceLock<u32> = OnceLock::new();
    *FLAGS.get_or_init(|| parse_override(std::env::var(OVERRIDE_VARIABLE).ok().as_deref(), DEFAULT))
}

/// How many times Windows has asked this process's window which gestures it wants.
pub fn queries() -> i64 {
    QUERIES.load(Ordering::Relaxed)
}

/// The decision: `Some(answer)` for the gesture query (and the query is counted), `None` for every
/// other message. The mask is a parameter so it can be tested.
pub fn answer(msg: u32, mask: u32) -> Option<isize> {
    if msg != QUERY_MESSAGE {
        return None;
    }
    QUERIES.fetch_add(1, Ordering::Relaxed);
    Some(mask as isize)
}

/// Text for a window title: how often Windows has asked and what the window answers.
pub fn title_suffix() -> String {
    format!("gesture queries {}, answer 0x{:X}", queries(), flags())
}

/// The cursor number with its name for the two known values (`13 (pen)`, `14 (eraser)`); any
/// other number is returned bare, because a guessed name would read as a measurement.
pub fn describe_cursor(cursor: u32) -> String {
    match cursor {
        13 => format!("{cursor} (pen)"),
        14 => format!("{cursor} (eraser)"),
        _ => cursor.to_string(),
    }
}

type SubclassProc = unsafe extern "system" fn(*mut c_void, u32, usize, isize, usize, usize) -> isize;

#[link(name = "comctl32")]
unsafe extern "system" {
    fn SetWindowSubclass(hwnd: *mut c_void, proc: SubclassProc, id: usize, ref_data: usize) -> i32;
    fn DefSubclassProc(hwnd: *mut c_void, msg: u32, wparam: usize, lparam: isize) -> isize;
}

unsafe extern "system" fn subclass_proc(
    hwnd: *mut c_void,
    msg: u32,
    wparam: usize,
    lparam: isize,
    _id: usize,
    _ref_data: usize,
) -> isize {
    match answer(msg, flags()) {
        Some(result) => result,
        None => unsafe { DefSubclassProc(hwnd, msg, wparam, lparam) },
    }
}

/// Installs the answer on `hwnd`. Must be called on the thread that owns the window (the UI
/// thread). The subclass stays for the life of the window. Returns false if Windows refused.
pub fn install(hwnd: *mut c_void) -> bool {
    if hwnd.is_null() {
        return false;
    }
    unsafe { SetWindowSubclass(hwnd, subclass_proc, 1, 0) != 0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Values restated on purpose: the tests must fail if the constants drift from Microsoft's.
    #[test]
    fn the_message_and_flag_values_are_microsofts() {
        assert_eq!(QUERY_MESSAGE, 0x02C0 + 12);
        assert_eq!(DISABLE_PRESS_AND_HOLD, 0x1);
        assert_eq!(DISABLE_PEN_TAP_FEEDBACK, 0x8);
        assert_eq!(DISABLE_PEN_BARREL_FEEDBACK, 0x10);
        assert_eq!(DEFAULT, 0x19);
    }

    #[test]
    fn the_query_is_answered_and_counted() {
        let before = queries();
        assert_eq!(answer(QUERY_MESSAGE, 0x19), Some(0x19));
        assert_eq!(queries(), before + 1);
    }

    #[test]
    fn a_mask_of_zero_is_still_an_answer() {
        assert_eq!(answer(QUERY_MESSAGE, 0), Some(0));
    }

    #[test]
    fn every_other_message_is_left_alone() {
        let before = queries();
        for msg in [0x0000, 0x0200, 0x02CB, 0x02CD] {
            assert_eq!(answer(msg, 0x19), None);
        }
        assert_eq!(queries(), before);
    }

    #[test]
    fn the_override_is_hexadecimal() {
        assert_eq!(parse_override(Some("0x10"), 0x19), 0x10);
        assert_eq!(parse_override(Some("0X10"), 0x19), 0x10);
        assert_eq!(parse_override(Some("10"), 0x19), 0x10);
        assert_eq!(parse_override(Some(" 19 "), 0x19), 0x19);
        assert_eq!(parse_override(Some("0"), 0x19), 0);
        assert_eq!(parse_override(Some("FFFFFFFF"), 0), 0xFFFF_FFFF);
    }

    #[test]
    fn a_missing_or_bad_override_keeps_the_default() {
        for text in [None, Some(""), Some("   "), Some("banana"), Some("0x"), Some("-1"), Some("1FFFFFFFF")] {
            assert_eq!(parse_override(text, 0x19), 0x19, "{text:?}");
        }
    }

    #[test]
    fn the_nib_and_the_eraser_are_named_and_any_other_cursor_is_bare() {
        assert_eq!(describe_cursor(13), "13 (pen)");
        assert_eq!(describe_cursor(14), "14 (eraser)");
        for c in [0, 1, 15, u32::MAX] {
            assert_eq!(describe_cursor(c), c.to_string());
        }
    }
}
