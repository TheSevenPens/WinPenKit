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
The Wintab query does not infer them from descriptions or substitute the specification
version for the driver version. The optional Windows lookup below adds device-node
information; firmware and installer/package versions still need vendor-specific support.

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

## Optional Windows device lookup

To correlate a raw Wintab identifier with richer Windows records:

```csharp
var tablet = WintabDiagnostics.QueryInfo()?.Devices?.FirstOrDefault();
var details = WintabDiagnostics.QueryWindowsDevice(tablet?.PlugAndPlayId);
if (details.Status is WindowsDeviceMatchStatus.MatchedInstanceId or WindowsDeviceMatchStatus.MatchedUsbSerial)
{
    var matched = details.Candidates[0];
    Console.WriteLine(matched.BusReportedName ?? matched.FriendlyName ?? matched.DeviceDescription);
    Console.WriteLine($"USB: {matched.UsbVendorId:X4}:{matched.UsbProductId:X4}");
    foreach (var node in details.ContainerDevices)
        Console.WriteLine($"{node.InstanceId}: {node.DriverProvider} {node.DriverVersion}");
}
```

Native callers use `pen_wintab_query_windows_device(info->devices[i].plug_and_play_id)`
and `pen_wintab_free_windows_device(result)`. Input/output strings are UTF-8. The
result owns all its strings and arrays until freed, independently of the original
Wintab snapshot. Null input produces `PEN_WINDOWS_DEVICE_MISSING_IDENTIFIER`;
invalid UTF-8 or allocation/conversion failure can return a null result. Native
optional numeric fields use `-1`; .NET uses null. Hardware IDs distinguish missing
(-1 count / null list) from an empty list. Container IDs are GUID strings in C and
nullable `Guid` values in .NET.

Both bindings use SetupAPI directly: no PowerShell, WMI process, context, or service
restart. The .NET lookup does not depend on WinPenKit.Native. It is synchronous,
enumerates **present** device nodes and should run off the input/render thread.
It is optional and does not change the cost or behavior of `QueryInfo()`.

Matching follows these rules, case-insensitively:

1. Prefer an exact Windows device-instance ID.
2. Otherwise, compare the entire identifier with the serial segment of USB device
   roots (`USB\VID_xxxx&PID_xxxx\serial`). Require Windows' `CM_DEVCAP_UNIQUEID`
   capability; USB interface nodes, generated nonunique IDs and unknown capability
   values are excluded from serial matching.
3. Return `Ambiguous` with all candidates if more than one node matches. Never pick
   by enumeration order, display name, hardware ID, substring, or model similarity.

Statuses are `MissingIdentifier`, `NotFound`, `Ambiguous`, `MatchedInstanceId`,
`MatchedUsbSerial`, and `Unavailable`. An enumeration failure or unreadable instance
ID yields `Unavailable`, so a partial enumeration cannot claim a unique match.
An individual missing/malformed property remains unavailable. Serial correlation
is best-effort, not a universal identity guarantee; properties can change mid-query.

On a unique match, `Candidates` has one entry and `ContainerDevices` contains
present nodes with the same nonempty Windows Container ID, including the match.
Without a container, it contains only the matched node. Unsuccessful/ambiguous
matches have no container group. **Container membership is not proof that every
node is part of the tablet:** the live test machine groups some USB hubs and a
connected peripheral with the Wacom tablet. The API exposes Windows' grouping
without selecting a single driver or attributing all members to the tablet.

Each entry includes its instance ID, friendly name, device description, bus-reported
name, manufacturer, container, raw hardware IDs, unique-instance capability, parsed
USB vendor/product/revision, and its own driver provider/version/date. USB revision
is `bcdDevice`, not a promised firmware value. Driver version/date come from the
node's INF metadata, not the Wacom installer version or installation time. USB
numbers are nullable 16-bit values (usually displayed in hexadecimal).

For example, the live machine's generic Wintab `WACOM Tablet` entry resolves to a
USB root reporting `Wacom Cintiq 24 touch`, VID/PID `056A:03FD`, and revision `0104`.
Its composite parent uses a Microsoft driver, while tablet interface nodes report
Wacom `4.0.0.4`. The API keeps those separate and does not guess an overall package
version. Refresh explicitly after hardware/driver changes; no results are cached.

References: [SetupAPI property queries](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw),
[USB identifiers](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/standard-usb-identifiers),
[device driver version](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-driverversion),
[Windows Container IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid).

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

The same synthetic test commands also exercise Windows matching: exact-ID priority,
duplicate serial ambiguity, rejection of nonunique/interface IDs, absent/zero
containers, USB hardware-ID parsing and property decoding. Managed tests additionally
inject property growth, type mismatches and disappearing data; native tests verify
C snapshot string/array ownership after the source records are destroyed.

To exercise live Windows correlation (managed JSON contains device instance IDs):

```powershell
dotnet run --project WinPenKit.TestConsole -- --wintab-windows-info
./WinPenKit.Native.Tests/bin/Debug/WinPenKit.Native.Tests.exe --live-windows
```

These probes return 0 on unique matches, 1 if Wintab has no devices or the query
cannot be made, and 2 for an unsuccessful/ambiguous Windows match. Native output
uses stderr because the live Wacom driver interferes with the probe's stdout.
