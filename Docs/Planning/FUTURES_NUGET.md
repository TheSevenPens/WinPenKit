# NuGet Publishing Plan

Design discussion for publishing WinPenKit as a shared library via NuGet.

## What to Publish

There are three packaging options:

### Option A: Managed NuGets (C# WinPenKit + framework adapters)

Standard .NET NuGet packages. Consumers add a PackageReference and get the managed library. The managed side is actually multiple packages — the framework-agnostic core plus a per-framework adapter that the consumer's UI stack pulls in:

```
WinPenKit.nupkg                 (core: IPenSession, PenPoint, factory, Wintab + WM_POINTER backends)
├── lib/net10.0-windows/
│   └── WinPenKit.dll

WinPenKit.Wpf.nupkg             (depends on WinPenKit; adds WpfStylusSession)
WinPenKit.WinForms.nupkg        (depends on WinPenKit; adds WinFormsPointerSession)
WinPenKit.Avalonia.nupkg        (depends on WinPenKit; adds AvaloniaPointerSession)
WinPenKit.WinUI.nupkg           (depends on WinPenKit; adds WinUiPointerSession; needs Windows App SDK)
```

`WinPenKit` has no third-party dependencies — it ships its own Wintab P/Invoke layer. (The legacy `WintabDN` library is used only by `ExtensionTestApp` for tablet ExpressKeys/Touch Rings and is out of scope here.)

**Pros:** Simplest to build, publish, and consume. Standard `dotnet add package` workflow. Consumers only pull in the framework adapter they actually use.
**Cons:** .NET only. Five packages to publish in lockstep.

### Option B: Native NuGet (C++ WinPenKit.Native.dll)

Native NuGet with MSBuild glue to copy the DLL and set up include/lib paths.

```
WinPenKit.Native.nupkg
├── build/native/
│   └── WinPenKit.Native.targets
├── runtimes/win-x64/native/
│   └── WinPenKit.Native.dll
├── include/
│   └── pen_session.h
├── lib/native/x64/
│   └── WinPenKit.Native.lib
```

The `.targets` file tells MSBuild to copy the DLL, link the import library, and add include paths:

```xml
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <NativeLibs Include="$(MSBuildThisFileDirectory)..\..\runtimes\win-x64\native\*.dll" />
    <None Include="@(NativeLibs)" Link="%(FileName)%(Extension)"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemDefinitionGroup>
    <Link>
      <AdditionalLibraryDirectories>
        $(MSBuildThisFileDirectory)..\..\lib\native\x64;%(AdditionalLibraryDirectories)
      </AdditionalLibraryDirectories>
    </Link>
    <ClCompile>
      <AdditionalIncludeDirectories>
        $(MSBuildThisFileDirectory)..\..\include;%(AdditionalIncludeDirectories)
      </AdditionalIncludeDirectories>
    </ClCompile>
  </ItemDefinitionGroup>
</Project>
```

**Pros:** Works for C++, Rust (via bindgen), and any native consumer.
**Cons:** More complex packaging. NuGet is not the natural distribution channel for native libs (vcpkg, Conan are more common).

### Option C: Combined (P/Invoke pattern)

Ship the native DLL inside a managed NuGet with a thin C# interop wrapper. This is how packages like SkiaSharp and SQLitePCLRaw work.

```
WinPenKit.nupkg
├── lib/net10.0-windows/
│   └── WinPenKit.Interop.dll    (thin P/Invoke wrapper)
├── runtimes/win-x64/native/
│   └── WinPenKit.Native.dll        (the C++ DLL)
```

**Pros:** Single package, .NET consumers get managed API backed by native performance.
**Cons:** Most complex to build. Only makes sense if the native DLL offers something the pure C# version doesn't.

### Recommendation

Start with **Option A** (managed only). It's the simplest, the C# WinPenKit already works, and most consumers will be .NET apps. Add the native package later if demand exists.

## Project Configuration

Add NuGet metadata to `WinPenKit.csproj`:

```xml
<PropertyGroup>
  <PackageId>WinPenKit</PackageId>
  <Version>1.0.0</Version>
  <Authors>YourName</Authors>
  <Description>Unified pen input SDK for Windows .NET apps (Wintab + WM_POINTER).</Description>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
</PropertyGroup>
```

### Framework Adapter Packages

Each framework adapter (`WinPenKit.Wpf`, `WinPenKit.WinForms`, `WinPenKit.Avalonia`, `WinPenKit.WinUI`) needs its own `.csproj` NuGet metadata declaring `WinPenKit` as a `PackageReference` (with version). Publish all five together on each tag — version-skew between core and adapters is the most likely cause of consumer breakage.

`WinPenKit.WinUI` additionally depends on Windows App SDK and must target a Windows 10 SDK version; expect that package to be more brittle than the others.

## Versioning

Use [Semantic Versioning](https://semver.org/):
- **Major:** Breaking API changes (renamed types, removed methods)
- **Minor:** New features (new session types, new PenPoint fields)
- **Patch:** Bug fixes (coordinate mapping fix, threading fix)

The version can be set in the `.csproj` or derived from the git tag at build time:

```yaml
# In GitHub Actions:
- name: Extract version from tag
  id: version
  run: echo "VERSION=${GITHUB_REF#refs/tags/release/v}" >> $GITHUB_OUTPUT
  shell: bash

- run: dotnet pack -c Release -p:Version=${{ steps.version.outputs.VERSION }} -o ./nupkgs
```

## CI/CD with GitHub Actions

### Workflow File

`.github/workflows/nuget-publish.yml`:

```yaml
name: Publish NuGet

on:
  push:
    tags:
      - 'release/v*'    # Only triggers on release/v1.0.0, not arbitrary tags

jobs:
  publish:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - run: dotnet restore
      - run: dotnet build -c Release --no-restore
      - run: dotnet test -c Release --no-restore
      - run: dotnet pack -c Release --no-restore -o ./nupkgs

      - run: dotnet nuget push ./nupkgs/*.nupkg
              --source https://api.nuget.org/v3/index.json
              --api-key ${{ secrets.NUGET_API_KEY }}
              --skip-duplicate
```

### Tag Pattern: Why `release/v*`

Using `release/v*` instead of `v*` prevents accidental publishes. Only tags matching this specific pattern trigger the workflow:

| Tag | Publishes? |
|---|---|
| `release/v1.0.0` | Yes |
| `release/v2.3.1` | Yes |
| `v1.0.0` | No |
| `LKG_WinTabDN_Modernization` | No |
| `backup-friday` | No |

### If Native DLL is Included

Add MSBuild to the workflow for the C++ build:

```yaml
      - uses: microsoft/setup-msbuild@v2

      # Build native DLL first
      - run: msbuild WinPenKitNative.sln -p:Configuration=Release -p:Platform=x64

      # Then build/pack managed
      - run: dotnet restore
      # ... etc
```

### Pre-release Packages (Optional)

Publish CI builds as pre-release packages for testing before a real release:

```yaml
on:
  push:
    branches: [main]
    tags: ['release/v*']

# In the pack step:
- run: |
    if [[ "$GITHUB_REF" == refs/tags/release/v* ]]; then
      VERSION="${GITHUB_REF#refs/tags/release/v}"
    else
      VERSION="0.0.0-ci.${{ github.run_number }}"
    fi
    dotnet pack -c Release -p:Version=$VERSION -o ./nupkgs
  shell: bash
```

This produces:
- **Tag push:** `1.2.0` (stable, listed on nuget.org)
- **Main branch push:** `0.0.0-ci.42` (pre-release, installable with `--prerelease`)

## Publishing Flow

### Setup (one-time)

1. Create a nuget.org account and sign in
2. Go to API Keys → Create a key scoped to your package ID(s), Push permission
3. In GitHub repo → Settings → Secrets and variables → Actions → add `NUGET_API_KEY`
4. Commit the workflow YAML file to the repo

### Releasing

```bash
git tag release/v1.0.0
git push origin release/v1.0.0
```

GitHub Actions automatically builds, tests, packs, and publishes. The package appears on nuget.org within ~5 minutes (indexing delay).

### Testing Locally Before Publishing

Always test with a local NuGet feed first:

```bash
# Create a local feed directory
mkdir ./local-feed

# Pack
dotnet pack -c Release -o ./local-feed

# Add the local feed (one-time)
dotnet nuget add source ./local-feed --name local

# Test in a separate project
dotnet add package WinPenKit --source local
```

### Important: NuGet Packages Are Immutable

Once you push version 1.0.0, you **cannot replace it**. You can unlist it (hides from search, but still installable by version), but you can never re-upload the same version number. Always test locally first. If you publish a broken version, you must bump the version number and publish a fix.

## Alternative: Manual Publishing

If CI/CD feels premature, you can publish manually:

```bash
dotnet pack -c Release -o ./nupkgs
dotnet nuget push ./nupkgs/WinPenKit.1.0.0.nupkg \
  --source https://api.nuget.org/v3/index.json \
  --api-key YOUR_KEY
```

This is fine for early releases. Move to CI/CD when the release cadence justifies it.
