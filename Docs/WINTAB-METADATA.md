# Wintab device and interface information

`WintabDiagnostics.QueryInfo()` (.NET) and `pen_wintab_query_info()` (C ABI) query
the installed Wintab driver without opening a tablet context or starting pen input.
They work independently of the system and high-resolution digitizer session modes.

## C#

```csharp
using WinPenKit.Diagnostics;

var info = WintabDiagnostics.QueryInfo();
if (info is not null)
{
    Console.WriteLine(info.Identification ?? "Identification unavailable");
    Console.WriteLine($"Wintab specification: {info.SpecificationVersion}");
    Console.WriteLine($"Wintab implementation: {info.ImplementationVersion}");
    if (info.Devices is not null)
        foreach (var device in info.Devices)
            Console.WriteLine($"{device.DeviceIndex}: {device.Name}; PnP ID: {device.PlugAndPlayId}");
}
```

The snapshot is null if the driver cannot be loaded, has no standard `WTInfoW`
export, or does not respond. Individual unsupported/malformed fields are null.
`Devices == null` means the device count could not be read or was invalid; an empty
collection means the driver reported zero devices. The returned collection is read-only.

## C / C++ / other native bindings

```c
#include "pen_session.h"
#include <stdio.h>

const PenWintabInfo* info = pen_wintab_query_info();
if (info) {
    if (info->implementation_version >= 0)
        printf("Wintab implementation: %d.%d\n",
            info->implementation_version >> 8, info->implementation_version & 255);
    for (int i = 0; i < info->device_count; ++i) {
        const PenWintabDeviceInfo* device = &info->devices[i];
        printf("%u: %s\n", device->device_index, device->name ? device->name : "unavailable");
    }
    pen_wintab_free_info(info);
}
```

The DLL owns the snapshot and every pointer in it. Strings are UTF-8 and remain
valid until `pen_wintab_free_info`; callers must not mutate or free them separately.
Freeing null is safe. Versions are packed unsigned 16-bit values held in signed
32-bit fields so `-1` can mean unavailable. `device_count == -1` means enumeration
was unavailable; zero means no devices reported. A null snapshot can also indicate
an allocation failure. No existing session ABI or pen packet layout changes.

## What the fields mean

| Property | Wintab source | Meaning |
| --- | --- | --- |
| Identification | `WTI_INTERFACE / IFC_WINTABID` | Driver's raw identification description |
| SpecificationVersion | `WTI_INTERFACE / IFC_SPECVERSION` | Wintab specification major/minor version |
| ImplementationVersion | `WTI_INTERFACE / IFC_IMPLVERSION` | Wintab implementation major/minor version, **not the full driver package version** |
| Devices | `IFC_NDEVICES`, then `WTI_DEVICES + index` | Driver-reported device entries |
| Name | `DVC_NAME` | Raw device description; may include model/revision text |
| PlugAndPlayId | `DVC_PNPID` | Raw PnP identifier, with no vendor-specific parsing |

The [Wintab reference](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-reference/)
defines the fields and optional-query behavior. Wacom documents
[DTU serial numbers through DVC_PNPID](https://developer-support.wacom.com/hc/en-us/articles/9354452830231-Obtain-the-serial-number-of-a-DTU),
but that does not guarantee a physical serial number for every vendor, device or driver.
Packet serial numbers are event sequence numbers and are unrelated to tablet serial numbers.

There are no dedicated standard queries for a model number, tablet firmware values,
the full installed driver package version, or Wacom Center's **Customized** value.
This API does not infer them from descriptions or substitute the specification
version for the driver version. Those values need separate platform/vendor support.

Device indices are driver-local, not stable hardware identifiers. Entries can
include virtual devices or devices retained in driver preferences; enumeration is
not proof of current physical connection or which tablet produced a session's points.
Refresh after device/driver changes. Queries use several `WTInfoW` calls, so a
snapshot is best-effort and can contain missing fields during unplug or restart.

Strings use size queries and bounded decoding; scalars require the documented
WORD/UINT width. Reads reserve at least the driver's largest reported category.
Malformed fields, allocations reported above 1 MiB, and device counts exceeding
the 100 device categories are rejected. Wintab has no destination-capacity argument:
as with other Wintab calls, memory safety still relies on a conforming native driver.

## Verification

No hardware is needed for the reader tests:

```powershell
dotnet run --project WinPenKit.TestConsole -- --selftest-wintab-info
msbuild WinPenKit.Native.Tests/WinPenKit.Native.Tests.vcxproj -p:Configuration=Debug -p:Platform=x64
./WinPenKit.Native.Tests/bin/Debug/WinPenKit.Native.Tests.exe
```

Both suites exercise the production readers with synthetic `WTInfoW` responses:
multiple devices, optional fields, Unicode, exact version widths/byte ordering,
unterminated/odd-length strings, unknown versus zero counts, corrupt sizes/counts,
disappearing data and independent snapshot lifetimes. The native test executable
compiles the production metadata source, including its allocation/free functions.

To query the live installed driver without opening a context:

```powershell
dotnet run --project WinPenKit.TestConsole -- --wintab-info
./WinPenKit.Native.Tests/bin/Debug/WinPenKit.Native.Tests.exe --live
```

The managed command prints JSON including raw PnP identifiers; the native command
prints names/versions and whether each PnP ID is present. They exit 1 if the query
is unavailable, 0 if a snapshot was returned. Synthetic tests do not establish how
each tablet vendor populates these fields; the live query is for that check.
