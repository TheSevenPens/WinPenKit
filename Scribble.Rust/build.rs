fn main() {
    // Tell the linker where to find WinPenKit.Native.lib (the import library
    // for WinPenKit.Native.dll which exports the pen_session_* C API).
    // Check Release first, then Debug.
    let base = std::path::Path::new("../WinPenKit.Native/bin");
    let release = base.join("Release/x64");
    let debug = base.join("Debug/x64");

    let lib_dir = if release.exists() {
        release
    } else if debug.exists() {
        debug
    } else {
        panic!("WinPenKit.Native.lib not found — build the C++ DLL first");
    };

    println!(
        "cargo:rustc-link-search=native={}",
        lib_dir.canonicalize().unwrap().display()
    );
}
