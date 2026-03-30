# GitHub Actions CI/Release Plan

Automated builds and downloadable release artifacts.

## Workflow: `.github/workflows/build.yml`

### Triggers
- **Push to main** and **pull requests** — CI build (no artifacts)
- **`release/v*` tags** — full build + upload artifacts + create GitHub Release

### Build order
1. **C++ first** — `msbuild NativeCpp.sln` (produces PenSession.Native.dll/.lib)
2. **.NET** — `dotnet build WinPenSession.slnx`
3. **Rust** — `cargo build --release` in Scribble.Rust (links against PenSession.Native.lib)

### Release artifacts
On tagged releases, the workflow uploads:
- PenSession.Native (DLL + lib + header)
- Scribble.Win32 (exe + DLL)
- Scribble.Rust (exe + DLL)
- Scribble.WinUI, Scribble.Wpf, Scribble.WinForms, Scribble.Avalonia (build output)

### Versioning

Versions are derived from git tags. There is no version number in any project file — the tag is the single source of truth.

- Use [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`
- **Major:** Breaking API changes (renamed types, removed methods)
- **Minor:** New features (new session types, new PenPoint fields)
- **Patch:** Bug fixes

Pre-release versions use semver suffixes:

```
release/v1.0.0-beta.1    ← beta
release/v1.0.0-rc.1      ← release candidate
release/v1.0.0            ← stable
```

### Releasing

```bash
git tag release/v1.0.0
git push origin release/v1.0.0
```

The workflow creates a GitHub Release named after the tag with auto-generated release notes and downloadable artifacts.

### Debug builds

Release artifacts are Release configuration only. For debug binaries, build from source — see [BUILD.md](../BUILD.md).
