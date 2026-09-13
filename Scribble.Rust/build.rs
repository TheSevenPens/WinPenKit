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

    let lib_dir = lib_dir.canonicalize().unwrap();
    println!("cargo:rustc-link-search=native={}", lib_dir.display());

    // Copy the DLL next to the executable, every build.
    //
    // Nothing used to do this, so the copy beside the exe was whatever had been put there by
    // hand and stayed there. A DLL predating an added export fails at load with "Entry Point
    // Not Found" and names no export, which points at the Rust code rather than at the stale
    // file. Rebuilding the C++ project does not fix it either, because the copy the sample
    // loads is not the one that was rebuilt.
    let dll = lib_dir.join("WinPenKit.Native.dll");
    println!("cargo:rerun-if-changed={}", dll.display());

    // OUT_DIR is target/<profile>/build/<pkg>-<hash>/out; the executable is three levels up.
    if let Ok(out_dir) = std::env::var("OUT_DIR") {
        let exe_dir = std::path::Path::new(&out_dir)
            .ancestors()
            .nth(3)
            .map(|p| p.to_path_buf());

        if let (Some(exe_dir), true) = (exe_dir, dll.exists()) {
            let dest = exe_dir.join("WinPenKit.Native.dll");
            if let Err(e) = std::fs::copy(&dll, &dest) {
                // A running instance holds the file open. Say so rather than failing the
                // build: the copy already there may well be current.
                println!("cargo:warning=could not refresh {}: {e}", dest.display());
            }
        }
    }
}
