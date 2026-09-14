# CI/Release

GitHub Actions builds all projects on every push to main and on pull requests. Tagged releases produce downloadable artifacts and a GitHub Release.

## Workflow: `.github/workflows/build.yml`

### Triggers
- **Push to main** and **pull requests** — CI build (no artifacts)
- **`release/v*` tags** — the library and the samples: build, upload artifacts, create a Release
- **`wintab-contexts/v*` tags** — the Wintab context viewer, by itself

Two release tags because the two change at very different rates. The viewer carries its own copy
of .NET and is about 50 MB; the library is tagged often and the viewer rarely, so tagging them
together made every ordinary release 50 MB heavier for a tool that had not changed.

Everything still **builds** on every push and pull request — the viewer is in the solution — so
neither can break unnoticed between releases. Only the publishing is split.

### Build order
1. **C++ first** — `msbuild WinPenKitNative.sln` (produces WinPenKit.Native.dll/.lib)
2. **.NET** — `dotnet build WinPenKit.slnx` (all managed projects except WinUI)
3. **WinUI** — `msbuild` on individual .csproj files (see CI notes)
4. **Rust** — `cargo build --release` in Scribble.Rust (links against WinPenKit.Native.lib)

### Release artifacts

One zip per top-level folder, so each is its own download on the Release page.

On a **`release/v*`** tag:
- WinPenKit.Native (DLL + lib + header)
- WinPenKit.Managed (the managed DLLs, a pre-NuGet stopgap)
- Scribble.Win32 (exe + DLL)
- Scribble.Rust (exe + DLL)
- Scribble.WinUI, Scribble.Wpf, Scribble.WinForms, Scribble.Avalonia (build output)

On a **`wintab-contexts/v*`** tag:
- WintabContexts — one self-contained `WintabContexts.exe`, no runtime needed

### Solution files

| Solution | Contents | Built with |
|---|---|---|
| `WinPenKit.slnx` | All managed projects except WinUI | `dotnet build` |
| `WinPenKitNative.sln` | WinPenKit.Native + Scribble.Win32 | `msbuild` |
| WinUI projects | WinPenKit.WinUI + Scribble.WinUI | `msbuild` (individual .csproj) |

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

The library and the samples:

```bash
git tag release/v1.0.0
git push origin release/v1.0.0
```

The context viewer, on its own and on its own version numbers:

```bash
git tag wintab-contexts/v1.0.0
git push origin wintab-contexts/v1.0.0
```

Either creates a GitHub Release named after the tag, with auto-generated notes and the artifacts
for that kind of tag and no others.

### How the two appear on GitHub

In one list, newest first. GitHub does not group releases by tag prefix, so they interleave, and
the title is the tag:

```
wintab-contexts/v1.0.0            wintab-contexts/v1.0.0    14 Sep 2026
release/v0.11.0          Latest   release/v0.11.0           13 Sep 2026
release/v0.10.0                   release/v0.10.0           13 Sep 2026
```

**The "Latest" badge stays on the library.** Whichever release is newest and not a prerelease
would normally take it, and that badge drives the repository's front page and the
`/releases/latest` URL -- so a viewer release would otherwise make a diagnostic tool the thing a
casual visitor downloads. `make_latest: false` on viewer releases prevents it. This is not
theoretical: a throwaway `wintab-contexts` tag took the badge off `release/v0.11.0` before the
setting was added.

One wrinkle worth knowing: the generated notes list commits since the previous tag of **any**
kind, so a viewer release will list library commits and the other way round. The assets are
right; only the prose is broad.

## Debug builds

Release artifacts are Release configuration only. For debug binaries, build from source — see [BUILD.md](BUILD.md).
