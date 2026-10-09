#pragma once
#include "pen_session.h"
#include <windows.h>
#include <optional>
#include <string>
#include <vector>

namespace wintab {
// Internal data and pure matching seam. Only present nodes are supplied by the SetupAPI reader.
struct WindowsNode {
    std::wstring instance_id;
    std::optional<std::wstring> friendly_name, description, bus_name, manufacturer;
    std::optional<GUID> container;
    std::optional<std::vector<std::wstring>> hardware_ids;
    std::optional<bool> unique_id;
    int vendor = -1, product = -1, revision = -1;
    std::optional<std::wstring> driver_provider, driver_version, driver_date;
};
struct WindowsMatch {
    PenWindowsDeviceMatchStatus status;
    std::vector<size_t> candidates, related;
};
WindowsMatch match_windows_device(const std::wstring& id, const std::vector<WindowsNode>& nodes);
const PenWindowsDeviceLookupResult* make_windows_result(const WindowsMatch& match, const std::vector<WindowsNode>& nodes);
void parse_usb_ids(WindowsNode& node);
std::optional<std::wstring> decode_property_string(const std::vector<unsigned char>& bytes);
std::optional<std::vector<std::wstring>> decode_property_strings(const std::vector<unsigned char>& bytes);
}
