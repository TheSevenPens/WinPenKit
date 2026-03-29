# NuGet Publishing Plan

Design discussion for publishing WintabSession as a shared library via NuGet.

## What to Publish

There are three packaging options:

### Option A: Managed NuGet (C# WintabSession + WintabDN)

Standard .NET NuGet package. Consumers add a PackageReference and get the managed library.

```
WintabSession.nupkg
├── lib/net10.0-windows/
│   ├── WintabSession.dll
│   └── WintabDN.dll
```

**Pros:** Simplest to build, publish, and consume. Standard `dotnet add package` workflow.
**Cons:** .NET only.

### Option B: Native NuGet (C++ PenSession.Native.dll)

Native NuGet with MSBuild glue to copy the DLL and set up include/lib paths.

```
WintabSession.Native.nupkg
├── build/native/
│   └── WintabSession.Native.targets
├── runtimes/win-x64/native/
│   └── PenSession.Native.dll
├── include/
│   └── wintab_session.h
├── lib/native/x64/
│   └── PenSession.Native.lib
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
WintabSession.nupkg
├── lib/net10.0-windows/
│   └── WintabSession.Interop.dll    (thin P/Invoke wrapper)
├── runtimes/win-x64/native/
│   └── PenSession.Native.dll        (the C++ DLL)
```

**Pros:** Single package, .NET consumers get managed API backed by native performance.
**Cons:** Most complex to build. Only makes sense if the native DLL offers something the pure C# version doesn't.

### Recommendation

Start with **Option A** (managed only). It's the simplest, the C# WintabSession already works, and most consumers will be .NET apps. Add the native package later if demand exists.

## Project Configuration

Add NuGet metadata to `WintabSession.csproj`:

```xml
<PropertyGroup>
  <PackageId>WintabSession</PackageId>
  <Version>1.0.0</Version>
  <Authors>YourName</Authors>
  <Description>Managed Wintab session library for .NET</Description>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
</PropertyGroup>
```

### WintabDN Dependency

Two approaches:
- **Separate packages:** Publish WintabDN as its own NuGet, add a PackageReference from WintabSession. More modular — consumers who want raw Wintab access without the session abstraction can use WintabDN directly.
- **Bundled:** Include WintabDN.dll inside the WintabSession package. Simpler for consumers, but less flexible.

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
      - run: msbuild NativeCpp.sln -p:Configuration=Release -p:Platform=x64

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
dotnet add package WintabSession --source local
```

### Important: NuGet Packages Are Immutable

Once you push version 1.0.0, you **cannot replace it**. You can unlist it (hides from search, but still installable by version), but you can never re-upload the same version number. Always test locally first. If you publish a broken version, you must bump the version number and publish a fix.

## Alternative: Manual Publishing

If CI/CD feels premature, you can publish manually:

```bash
dotnet pack -c Release -o ./nupkgs
dotnet nuget push ./nupkgs/WintabSession.1.0.0.nupkg \
  --source https://api.nuget.org/v3/index.json \
  --api-key YOUR_KEY
```

This is fine for early releases. Move to CI/CD when the release cadence justifies it.
