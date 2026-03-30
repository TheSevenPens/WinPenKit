# CI/Release

GitHub Actions builds all projects on every push to main and on pull requests. Tagged releases produce downloadable artifacts and a GitHub Release.

## Workflow: `.github/workflows/build.yml`

### Triggers
- **Push to main** and **pull requests** — CI build (no artifacts)
- **`release/v*` tags** — full build + upload artifacts + create GitHub Release

### Build order
1. **C++ first** — `msbuild NativeCpp.sln` (produces PenSession.Native.dll/.lib)
2. **.NET** — individual `dotnet build` for each managed project; `msbuild` for WinUI (PRI generation requires it)
3. **Rust** — `cargo build --release` in Scribble.Rust (links against PenSession.Native.lib)

### Release artifacts
On tagged releases, the workflow uploads:
- PenSession.Native (DLL + lib + header)
- Scribble.Win32 (exe + DLL)
- Scribble.Rust (exe + DLL)
- Scribble.WinUI, Scribble.Wpf, Scribble.WinForms, Scribble.Avalonia (build output)

### CI notes
- The runner has VS 2022 (v143 toolset). The C++ build overrides `PlatformToolset=v143` since the local projects use v145 (VS 2025).
- WinUI projects must be built with `msbuild`, not `dotnet build`, due to PRI resource generation.
- The .slnx contains C++ .vcxproj files that `dotnet build` can't handle, so .NET projects are built individually.

## Versioning

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

Pre-release tags are automatically marked as prerelease on GitHub (won't show as "Latest").

## Releasing

```bash
git tag release/v1.0.0
git push origin release/v1.0.0
```

The workflow creates a GitHub Release named after the tag with auto-generated release notes and downloadable artifacts.

## Debug builds

Release artifacts are Release configuration only. For debug binaries, build from source — see [BUILD.md](BUILD.md).
