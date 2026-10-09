#include "windows_device_info.h"
#include <setupapi.h>
#include <initguid.h>
#include <devpkey.h>
#include <algorithm>
#include <cstring>
#include <cwctype>
#include <memory>
#include <regex>
#include <stdexcept>

#pragma comment(lib, "Setupapi.lib")

namespace {
const std::wregex usb_root(LR"(^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\([^\\]+)$)", std::regex::icase);
const std::wregex usb_hardware(LR"(^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&REV_([0-9A-F]{4}))?(?:&MI_[0-9A-F]{2})?$)", std::regex::icase);
bool equal(const std::wstring& a, const std::wstring& b) {
    return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()), b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}
bool blank(const std::wstring& text) {
    return text.empty() || std::all_of(text.begin(), text.end(), [](wchar_t c) { return std::iswspace(c) != 0; });
}
std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!size) throw std::runtime_error("UTF-8 conversion failed");
    std::string result(size, '\0');
    if (!WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr))
        throw std::runtime_error("UTF-8 conversion failed");
    return result;
}

std::optional<std::vector<unsigned char>> property(HDEVINFO set, SP_DEVINFO_DATA& device, const DEVPROPKEY& key, DEVPROPTYPE expected) {
    std::vector<unsigned char> bytes;
    for (int attempt = 0; attempt < 3; ++attempt) {
        DEVPROPTYPE type = 0; DWORD size = 0;
        if (SetupDiGetDevicePropertyW(set, &device, &key, &type, bytes.empty() ? nullptr : bytes.data(),
            static_cast<DWORD>(bytes.size()), &size, 0)) {
            if (type != expected || size > bytes.size()) return std::nullopt;
            bytes.resize(size); return bytes;
        }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || !size || size > 1024 * 1024) return std::nullopt;
        bytes.resize(size);
    }
    return std::nullopt;
}

std::optional<std::vector<wintab::WindowsNode>> read_nodes() {
    struct Set {
        HDEVINFO handle = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        ~Set() { if (handle != INVALID_HANDLE_VALUE) SetupDiDestroyDeviceInfoList(handle); }
    } set;
    if (set.handle == INVALID_HANDLE_VALUE) return std::nullopt;
    std::vector<wintab::WindowsNode> nodes;
    for (DWORD i = 0; ; ++i) {
        SP_DEVINFO_DATA device{}; device.cbSize = sizeof(device);
        if (!SetupDiEnumDeviceInfo(set.handle, i, &device)) {
            if (GetLastError() == ERROR_NO_MORE_ITEMS) return nodes;
            return std::nullopt;
        }
        auto text = [&](const DEVPROPKEY& key) -> std::optional<std::wstring> {
            auto data = property(set.handle, device, key, DEVPROP_TYPE_STRING);
            return data ? wintab::decode_property_string(*data) : std::nullopt;
        };
        auto instance = text(DEVPKEY_Device_InstanceId);
        // Do not claim a unique match when another node's identity cannot be read.
        if (!instance || instance->empty()) return std::nullopt;
        wintab::WindowsNode node;
        node.instance_id = *instance;
        node.friendly_name = text(DEVPKEY_Device_FriendlyName);
        node.description = text(DEVPKEY_Device_DeviceDesc);
        node.bus_name = text(DEVPKEY_Device_BusReportedDeviceDesc);
        node.manufacturer = text(DEVPKEY_Device_Manufacturer);
        auto container = property(set.handle, device, DEVPKEY_Device_ContainerId, DEVPROP_TYPE_GUID);
        if (container && container->size() == sizeof(GUID)) {
            GUID value; std::memcpy(&value, container->data(), sizeof(value)); node.container = value;
        }
        auto caps = property(set.handle, device, DEVPKEY_Device_Capabilities, DEVPROP_TYPE_UINT32);
        if (caps && caps->size() == sizeof(DWORD)) {
            DWORD value; std::memcpy(&value, caps->data(), sizeof(value)); node.unique_id = (value & 0x10) != 0;
        }
        auto ids = property(set.handle, device, DEVPKEY_Device_HardwareIds, DEVPROP_TYPE_STRING_LIST);
        if (ids) node.hardware_ids = wintab::decode_property_strings(*ids);
        wintab::parse_usb_ids(node);
        node.driver_provider = text(DEVPKEY_Device_DriverProvider);
        node.driver_version = text(DEVPKEY_Device_DriverVersion);
        auto date = property(set.handle, device, DEVPKEY_Device_DriverDate, DEVPROP_TYPE_FILETIME);
        if (date && date->size() == sizeof(FILETIME)) {
            FILETIME value; std::memcpy(&value, date->data(), sizeof(value)); SYSTEMTIME time{};
            if ((value.dwLowDateTime || value.dwHighDateTime) && FileTimeToSystemTime(&value, &time)) {
                wchar_t buffer[16]; swprintf_s(buffer, L"%04u-%02u-%02u", time.wYear, time.wMonth, time.wDay);
                node.driver_date = buffer;
            }
        }
        nodes.push_back(std::move(node));
    }
}

struct NodeStorage {
    std::string instance;
    std::optional<std::string> friendly, description, bus, manufacturer, container, provider, version, date;
    std::vector<std::string> ids;
    std::vector<const char*> id_pointers;
    PenWindowsDeviceInfo view{};

    void fill(const wintab::WindowsNode& node) {
        auto convert = [](const std::optional<std::wstring>& value) -> std::optional<std::string> {
            return value ? std::optional<std::string>(utf8(*value)) : std::nullopt;
        };
        auto ptr = [](const std::optional<std::string>& value) { return value ? value->c_str() : nullptr; };
        instance = utf8(node.instance_id); friendly = convert(node.friendly_name); bus = convert(node.bus_name);
        description = convert(node.description);
        manufacturer = convert(node.manufacturer); provider = convert(node.driver_provider);
        version = convert(node.driver_version); date = convert(node.driver_date);
        if (node.container) {
            const auto& g = *node.container;
            char buffer[40]; sprintf_s(buffer, "%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                g.Data1, g.Data2, g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
            container = buffer;
        }
        if (node.hardware_ids) {
            for (const auto& id : *node.hardware_ids) ids.push_back(utf8(id));
            for (const auto& id : ids) id_pointers.push_back(id.c_str());
        }
        view = {instance.c_str(), ptr(friendly), ptr(description), ptr(bus), ptr(manufacturer), ptr(container),
            node.hardware_ids ? static_cast<int32_t>(ids.size()) : -1, id_pointers.empty() ? nullptr : id_pointers.data(),
            node.unique_id ? (*node.unique_id ? 1 : 0) : -1, node.vendor, node.product, node.revision,
            ptr(provider), ptr(version), ptr(date)};
    }
};
struct Snapshot : PenWindowsDeviceLookupResult {
    std::vector<NodeStorage> candidate_storage, related_storage;
    std::vector<PenWindowsDeviceInfo> candidate_views, related_views;
    Snapshot() : PenWindowsDeviceLookupResult{PEN_WINDOWS_DEVICE_UNAVAILABLE, 0, nullptr, 0, nullptr} {}
    void fill(const wintab::WindowsMatch& match, const std::vector<wintab::WindowsNode>& nodes) {
        status = match.status;
        auto copy = [&](const std::vector<size_t>& indices, std::vector<NodeStorage>& storage, std::vector<PenWindowsDeviceInfo>& views) {
            storage.resize(indices.size());
            for (size_t i = 0; i < indices.size(); ++i) { storage[i].fill(nodes[indices[i]]); views.push_back(storage[i].view); }
        };
        copy(match.candidates, candidate_storage, candidate_views); copy(match.related, related_storage, related_views);
        candidate_count = static_cast<uint32_t>(candidate_views.size()); candidates = candidate_count ? candidate_views.data() : nullptr;
        container_device_count = static_cast<uint32_t>(related_views.size()); container_devices = container_device_count ? related_views.data() : nullptr;
    }
};
}

namespace wintab {
const PenWindowsDeviceLookupResult* make_windows_result(const WindowsMatch& match, const std::vector<WindowsNode>& nodes) {
    auto result = std::make_unique<Snapshot>();
    result->fill(match, nodes);
    return result.release();
}
std::optional<std::wstring> decode_property_string(const std::vector<unsigned char>& bytes) {
    if (bytes.size() < 2 || bytes.size() % 2 || bytes[bytes.size() - 1] || bytes[bytes.size() - 2]) return std::nullopt;
    std::wstring result((bytes.size() - 2) / 2, L'\0');
    if (!result.empty()) std::memcpy(result.data(), bytes.data(), bytes.size() - 2);
    return result.find(L'\0') == std::wstring::npos ? std::optional<std::wstring>(result) : std::nullopt;
}
std::optional<std::vector<std::wstring>> decode_property_strings(const std::vector<unsigned char>& bytes) {
    if (bytes.size() < 4 || bytes.size() % 2 || bytes[bytes.size()-1] || bytes[bytes.size()-2] || bytes[bytes.size()-3] || bytes[bytes.size()-4]) return std::nullopt;
    std::wstring value((bytes.size() - 4) / 2, L'\0');
    if (!value.empty()) std::memcpy(value.data(), bytes.data(), bytes.size() - 4);
    std::vector<std::wstring> result;
    if (value.empty()) return result;
    size_t start = 0;
    while (start <= value.size()) {
        size_t end = value.find(L'\0', start);
        if (end == std::wstring::npos) end = value.size();
        if (start == end) return std::nullopt;
        result.push_back(value.substr(start, end - start)); start = end + 1;
    }
    return result;
}
void parse_usb_ids(WindowsNode& node) {
    node.vendor = node.product = node.revision = -1;
    if (!node.hardware_ids) return;
    for (const auto& id : *node.hardware_ids) {
        std::wsmatch match;
        if (!std::regex_match(id, match, usb_hardware)) continue;
        int vendor = std::stoi(match[1], nullptr, 16), product = std::stoi(match[2], nullptr, 16);
        int revision = match[3].matched ? std::stoi(match[3], nullptr, 16) : -1;
        if ((node.vendor >= 0 && (node.vendor != vendor || node.product != product))
            || (node.revision >= 0 && revision >= 0 && node.revision != revision)) {
            node.vendor = node.product = node.revision = -1; return;
        }
        node.vendor = vendor; node.product = product;
        if (revision >= 0) node.revision = revision;
    }
}
WindowsMatch match_windows_device(const std::wstring& id, const std::vector<WindowsNode>& nodes) {
    if (blank(id)) return {PEN_WINDOWS_DEVICE_MISSING_IDENTIFIER, {}, {}};
    WindowsMatch result{PEN_WINDOWS_DEVICE_MATCHED_INSTANCE_ID, {}, {}};
    for (size_t i = 0; i < nodes.size(); ++i) if (equal(id, nodes[i].instance_id)) result.candidates.push_back(i);
    if (result.candidates.empty()) {
        result.status = PEN_WINDOWS_DEVICE_MATCHED_USB_SERIAL;
        for (size_t i = 0; i < nodes.size(); ++i) {
            std::wsmatch match;
            if (nodes[i].unique_id == true && std::regex_match(nodes[i].instance_id, match, usb_root)
                && equal(match[1], id)) result.candidates.push_back(i);
        }
    }
    if (result.candidates.empty()) result.status = PEN_WINDOWS_DEVICE_NOT_FOUND;
    else if (result.candidates.size() > 1) result.status = PEN_WINDOWS_DEVICE_AMBIGUOUS;
    else {
        const auto& container = nodes[result.candidates[0]].container;
        if (container && !IsEqualGUID(*container, GUID{})) {
            for (size_t i = 0; i < nodes.size(); ++i)
                if (nodes[i].container && IsEqualGUID(*nodes[i].container, *container)) result.related.push_back(i);
        } else result.related = result.candidates;
    }
    return result;
}
}

extern "C" {
const PenWindowsDeviceLookupResult* pen_wintab_query_windows_device(const char* pnp_id) {
    try {
        auto result = std::make_unique<Snapshot>();
        std::wstring id;
        if (pnp_id && *pnp_id) {
            int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, pnp_id, -1, nullptr, 0);
            if (!size) return nullptr;
            id.resize(size);
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, pnp_id, -1, id.data(), size)) return nullptr;
            id.resize(size - 1);
        }
        if (blank(id)) result->status = PEN_WINDOWS_DEVICE_MISSING_IDENTIFIER;
        else if (auto nodes = read_nodes()) return wintab::make_windows_result(wintab::match_windows_device(id, *nodes), *nodes);
        return result.release();
    } catch (...) { return nullptr; }
}
void pen_wintab_free_windows_device(const PenWindowsDeviceLookupResult* result) {
    delete static_cast<const Snapshot*>(result);
}
}
