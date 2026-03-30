# CI/Release

GitHub Actions builds all projects on every push to main and on pull requests. Tagged releases produce downloadable artifacts and a GitHub Release.

## Workflow: `.github/workflows/build.yml`

### Triggers
- **Push to main** and **pull requests** — CI build (no artifacts)
- **`release/v*` tags** — full build + upload artifacts + create GitHub Release

### Build order
1. **C++ first** — `msbuild NativeCpp.sln` (produces PenSession.Native.dll/.lib)
2. **.NET** — `dotnet build WinPenSession.slnx` (all managed projects except WinUI)
3. **WinUI** — `msbuild` on individual .csproj files (see CI notes)
4. **Rust** — `cargo build --release` in Scribble.Rust (links against PenSession.Native.lib)

### Release artifacts
On tagged releases, the workflow uploads:
- PenSession.Native (DLL + lib + header)
- Scribble.Win32 (exe + DLL)
- Scribble.Rust (exe + DLL)
- Scribble.WinUI, Scribble.Wpf, Scribble.WinForms, Scribble.Avalonia (build output)

### Solution files

| Solution | Contents | Built with |
|---|---|---|
| `WinPenSession.slnx` | All managed projects except WinUI | `dotnet build` |
| `NativeCpp.sln` | PenSession.Native + Scribble.Win32 | `msbuild` |
| WinUI projects | PenSession.WinUI + Scribble.WinUI | `msbuild` (individual .csproj) |

### CI notes
- The runner has VS 2022 (v143 toolset). The C++ build overrides `PlatformToolset=v143` since the local projects use v145 (VS 2025).
- WinUI requires `msbuild` in CI due to a .NET 10 preview gap: the `ExpandPriContent` PRI task DLL is missing from the dotnet CLI SDK. This may be resolved in a future .NET 10 / Windows App SDK release.

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
