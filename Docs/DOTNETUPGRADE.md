# .NET Upgrade Guide

## Migration Summary

This document describes the migration from **.NET Framework 4.8 (C# 7.3)** to **.NET 10 (C# 14)**, performed on 26 March 2026. The version was bumped from **2.1.0** to **3.0.0**.

## Project File Conversion

All three projects were converted from legacy-style `.csproj` files to the modern SDK-style format.

### Before (legacy-style, ~170 lines each)
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="12.0" DefaultTargets="Build"
         xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <!-- ... 150+ lines of configuration, file lists, bootstrapper packages ... -->
  </PropertyGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

### After (SDK-style, ~15 lines each)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <LangVersion>14</LangVersion>
    <UseWindowsForms>true</UseWindowsForms>
    <!-- ... -->
  </PropertyGroup>
</Project>
```

### Key differences

| Feature | Legacy (.NET Framework) | SDK-style (.NET 10) |
|---|---|---|
| Target framework | `<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>` | `<TargetFramework>net10.0-windows</TargetFramework>` |
| Language version | `<LangVersion>7.3</LangVersion>` (per-config) | `<LangVersion>14</LangVersion>` (global) |
| Source files | Explicitly listed with `<Compile Include="..." />` | Auto-included via globbing |
| Assembly references | `<Reference Include="System.Drawing" />` etc. | Implicit via `<UseWindowsForms>` / `<UseWPF>` |
| Assembly metadata | Separate `Properties/AssemblyInfo.cs` | `<Version>`, `<Company>`, `<Copyright>` in `.csproj` |
| Platform configs | Separate PropertyGroups per platform (x86, x64, Mixed) | Single `Any CPU` configuration |

### Per-project details

**WintabDN** (core library):
- `AllowUnsafeBlocks` retained for P/Invoke marshalling
- `UseWindowsForms` enabled (depends on `System.Windows.Forms.Message`)

**LegacyPenTestApp** (WinForms test app):
- `UseWindowsForms` and `UseWPF` enabled (references `PresentationCore`/`PresentationFramework`)
- `ApplicationManifest` retained for DPI awareness settings

**ExtensionTestApp** (WinForms test app):
- `UseWindowsForms` enabled

## Solution File

The solution was simplified from 6 build configurations (Debug/Release x Any CPU/Mixed Platforms/x86/x64) down to 2 (Debug/Release x Any CPU). The SDK handles platform targeting automatically.

## Files Removed

### `Properties/AssemblyInfo.cs` (all 3 projects)

Assembly metadata (`AssemblyVersion`, `AssemblyCompany`, `AssemblyCopyright`, etc.) is now specified directly in the `.csproj` via MSBuild properties. SDK-style projects auto-generate these attributes at build time, so keeping the old files would cause duplicate attribute errors.

### `app.config` (LegacyPenTestApp, ExtensionTestApp)

These files only contained the .NET Framework runtime binding:
```xml
<startup>
  <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/>
</startup>
```
This is not applicable to .NET 10, which uses a different hosting model.

### `Properties/Settings.Designer.cs` and `Properties/Settings.settings` (LegacyPenTestApp, ExtensionTestApp)

These contained empty `ApplicationSettingsBase` subclasses with no actual settings defined. In .NET 10, `ApplicationSettingsBase` requires the `System.Configuration.ConfigurationManager` NuGet package. Since no settings were in use, these files were removed rather than adding the dependency.

### `WintabDN/Properties/` directory

Empty after `AssemblyInfo.cs` was removed (WintabDN had no Resources or Settings files).

### Build artifacts

Old `bin/`, `obj/`, and `.vs/` directories from .NET Framework builds were cleaned up. These are excluded by `.gitignore`.

## Code Changes

### `ReaderWriterLock` → `ReaderWriterLockSlim` (MessageEvents.cs)

`ReaderWriterLock` is a legacy synchronization primitive that has been superseded by `ReaderWriterLockSlim` since .NET 3.5. While `ReaderWriterLock` still exists in .NET 10, `ReaderWriterLockSlim` is the recommended replacement due to better performance and simpler recursion semantics.

The lock/unlock pattern was also updated to use try/finally blocks to ensure locks are released even if an exception occurs:

```csharp
// Before
_lock.AcquireWriterLock(Timeout.Infinite);
_messagePacketSet[messageID] = state;
_lock.ReleaseWriterLock();

// After
_lock.EnterWriteLock();
try { _messagePacketSet[messageID] = state; }
finally { _lock.ExitWriteLock(); }
```

### Removed Code Access Security attributes (CNativeWindowListener.cs)

Code Access Security (CAS) is not supported in .NET Core and later. The `[PermissionSet]` attributes were removed:

```csharp
// Removed (2 occurrences)
[System.Security.Permissions.PermissionSet(
    System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
```

These attributes had no effect in .NET 10 and would generate build warnings.

### Fixed incorrect enum reference (CNativeWindowListener.cs)

A pre-existing bug referenced a nonexistent `WintabEventMessage` type. The correct enum was `EWintabEventMessage` (later renamed to `WintabEventMessage`), defined in `WintabDN/WintabData.cs`.

## Build Requirements

### Previous
- Windows 7+
- .NET Framework 4.0+
- Visual Studio 2017+

### Current
- Windows 10+
- .NET 10 SDK
- Visual Studio 2022 (17.x)+

### Building
```
dotnet build WintabDN.slnx
```

## P/Invoke Compatibility

All `[DllImport("Wintab32.dll")]` declarations in `WintabNative.cs` remain unchanged. P/Invoke works identically in .NET 10. The `net10.0-windows` target framework moniker ensures Windows-specific APIs are available.

## Post-Migration Modernization

After the initial .NET 10 migration, further improvements were made:

### Solution format
- Converted `WintabDN.sln` to `WintabDN.slnx` (XML-based format, auto-infers configurations)

### LegacyPenTestApp renamed to LegacyPenTestApp
- Directory, project, assembly, and namespace all renamed
- Namespace unified from `FormTestApp`/`LegacyPenTestApp`/`WintabDN` to `LegacyPenTestApp`
- Nullable reference types and implicit usings enabled
- All string concatenation replaced with interpolation
- Dead code removed, resource leaks fixed, input validation added
- `ApplicationConfiguration.Initialize()` replaces legacy init
- DPI-aware scribble drawing with bitmap-backed rendering; raw Wintab coords passed to `PointToClient()` (handles DPI internally in PerMonitorV2)
- Render timer at ~60fps with dirty flag; bitmap draw per-packet, screen repaint batched
- System/Digitizer context switcher for scribble mode
- Live pen data status bar added
- `app.manifest` removed; DPI mode set via `ApplicationHighDpiMode` csproj property
- `CNativeWindowListener.cs` deleted (dead code)
- WPF dependency removed (unused)

### WintabDN library improvements
- All 42 `MessageBox.Show()` calls removed; exceptions propagate to caller
- `UnmanagedBuffer : IDisposable` replaces raw `AllocHGlobal`/`FreeHGlobal`; memory leak in `MarshalDataExtPackets` fixed
- `WintabContext` and `WintabData` implement `IDisposable`
- Custom exception hierarchy: `WintabException`, `WintabContextException`, `WintabDataException`, `WintabExtensionException`
- `IWintabLog` logging abstraction with `WintabLog` static entry point
- `MarshalDataExtPackets` optimized (zero per-packet allocations)
- `WintabDpiHelper` utility for DPI scale queries and physical-to-logical conversion (for non-WinForms use)
- `ReaderWriterLockSlim` with proper try/finally (from initial migration)
- **Framework-agnostic**: removed `System.Windows.Forms` and `System.Drawing` dependencies entirely
  - `MessageEvents.cs` rewritten with raw Win32 P/Invoke (`RegisterClassExW`, `CreateWindowExW`, `GetMessage`/`DispatchMessage`)
  - `System.Windows.Forms.Message` replaced with `WintabMessage` struct in public API
  - `WintabExtensions.cs` uses `File.ReadAllBytes()` instead of `System.Drawing.Image`
  - `UseWindowsForms` removed from `WintabDN.csproj`
  - Library can now be consumed from WPF, Avalonia, MAUI, or console applications
