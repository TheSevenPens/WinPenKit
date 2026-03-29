fn main() {
    // Tell the linker where to find WintabSession.lib (the import library
    // for WintabSession.dll which exports the pen_session_* C API).
    println!(
        "cargo:rustc-link-search=native={}",
        std::path::Path::new("../PenSession.Native/bin/Debug/x64")
            .canonicalize()
            .expect("WintabSession.lib not found — build the C++ DLL first")
            .display()
    );
}
