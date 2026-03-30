# GitHub Actions CI/Release Plan

Automated builds and downloadable release artifacts on every tagged release.

## Design

- `.github/workflows/build.yml` — builds all projects (C#, C++, Rust) on `windows-latest`
- Triggered by `release/v*` tags
- Produces artifacts: managed DLLs, native DLL, Scribble app binaries
- GitHub Releases with downloadable zip files
