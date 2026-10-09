#include "windows_device_info.h"
#include <cstdio>
#include <cstring>
#include <memory>

namespace {
using Info = std::unique_ptr<const PenWindowsDeviceLookupResult, decltype(&pen_wintab_free_windows_device)>;
wintab::WindowsNode node(const wchar_t* instance, std::optional<GUID> container = std::nullopt) {
    wintab::WindowsNode result; result.instance_id = instance; result.container = container; result.unique_id = true; return result;
}
std::vector<unsigned char> bytes(const wchar_t* text, size_t chars) {
    std::vector<unsigned char> result(chars * 2); std::memcpy(result.data(), text, result.size()); return result;
}
}

int windows_device_info_tests() {
    int failed = 0;
    auto check = [&](const char* name, bool passed) { std::printf("[%s] Windows metadata: %s\n", passed ? "PASS" : "FAIL", name); if (!passed) ++failed; };
    GUID group{1}, other_group{2};
    auto root = node(L"USB\\VID_056A&PID_03FD\\SERIAL-A", group);
    auto child = node(L"HID\\VID_056A&PID_03FD&MI_00\\CHILD", group);
    child.driver_provider = L"Wacom"; child.driver_version = L"4.0.0.4";
    auto other = node(L"USB\\VID_056A&PID_03FD\\SERIAL-B", other_group);
    std::vector<wintab::WindowsNode> nodes{root, child, other};
    auto match = wintab::match_windows_device(L"serial-a", nodes);
    check("whole serial and related container", match.status == PEN_WINDOWS_DEVICE_MATCHED_USB_SERIAL && match.candidates.size() == 1 && match.related.size() == 2);
    check("case-insensitive exact instance", wintab::match_windows_device(L"hid\\vid_056a&pid_03fd&mi_00\\child", nodes).status == PEN_WINDOWS_DEVICE_MATCHED_INSTANCE_ID);
    check("no substring match", wintab::match_windows_device(L"SERIAL", nodes).status == PEN_WINDOWS_DEVICE_NOT_FOUND);
    check("hardware ID is not instance ID", wintab::match_windows_device(L"USB\\VID_056A&PID_03FD", nodes).status == PEN_WINDOWS_DEVICE_NOT_FOUND);
    auto duplicate = node(L"USB\\VID_1234&PID_ABCD\\SERIAL-A", other_group);
    match = wintab::match_windows_device(L"SERIAL-A", {root, child, duplicate});
    check("duplicate serial stays ambiguous", match.status == PEN_WINDOWS_DEVICE_AMBIGUOUS && match.candidates.size() == 2 && match.related.empty());
    check("exact instance takes priority", wintab::match_windows_device(root.instance_id, {root, duplicate}).status == PEN_WINDOWS_DEVICE_MATCHED_INSTANCE_ID);
    check("USB interface suffix is not serial", wintab::match_windows_device(L"SERIAL-A", {node(L"USB\\VID_056A&PID_03FD&MI_00\\SERIAL-A")}).status == PEN_WINDOWS_DEVICE_NOT_FOUND);
    auto nonunique = root; nonunique.unique_id = false;
    check("generated/nonunique instance is not serial", wintab::match_windows_device(L"SERIAL-A", {nonunique}).status == PEN_WINDOWS_DEVICE_NOT_FOUND);
    nonunique.unique_id.reset();
    check("missing uniqueness capability is not serial", wintab::match_windows_device(L"SERIAL-A", {nonunique}).status == PEN_WINDOWS_DEVICE_NOT_FOUND);
    auto no_group = root; no_group.container.reset(); other.container.reset();
    check("missing container does not group unrelated nodes", wintab::match_windows_device(L"SERIAL-A", {no_group, other}).related.size() == 1);
    no_group.container = GUID{}; other.container = GUID{};
    check("zero container does not group unrelated nodes", wintab::match_windows_device(L"SERIAL-A", {no_group, other}).related.size() == 1);
    Info missing(pen_wintab_query_windows_device(nullptr), pen_wintab_free_windows_device);
    check("missing identifier", missing && missing->status == PEN_WINDOWS_DEVICE_MISSING_IDENTIFIER);
    check("invalid UTF-8 input rejected", !pen_wintab_query_windows_device("\xff"));
    root.hardware_ids = {L"USB\\VID_056A&PID_03FD", L"USB\\VID_056A&PID_03FD&REV_0104&MI_00"};
    wintab::parse_usb_ids(root);
    check("USB hardware identifiers and revision", root.vendor == 0x056a && root.product == 0x03fd && root.revision == 0x0104);
    root.hardware_ids = {L"USB\\VID_056A&PID_ZZZZ"}; wintab::parse_usb_ids(root);
    check("malformed hardware IDs ignored", root.vendor == -1);
    root.hardware_ids = {L"USB\\VID_056A&PID_03FD", L"USB\\VID_056A&PID_0001"}; wintab::parse_usb_ids(root);
    check("conflicting hardware IDs unavailable", root.vendor == -1);
    check("MULTI_SZ decode", wintab::decode_property_strings(bytes(L"one\0two\0", 9))->size() == 2);
    check("unterminated/odd strings rejected", !wintab::decode_property_string({65, 0}) && !wintab::decode_property_string({65, 0, 0}));
    check("bad MULTI_SZ rejected", !wintab::decode_property_strings(bytes(L"one", 4)));
    nodes[0].bus_name = L"\u30c6\u30b9\u30c8";
    nodes[0].hardware_ids = {L"USB\\VID_056A&PID_03FD"};
    Info snapshot(wintab::make_windows_result(wintab::match_windows_device(L"SERIAL-A", nodes), nodes), pen_wintab_free_windows_device);
    nodes.clear();
    check("C snapshot owns Unicode strings and hardware arrays", snapshot->candidate_count == 1
        && std::strcmp(snapshot->candidates[0].bus_reported_name, "\xe3\x83\x86\xe3\x82\xb9\xe3\x83\x88") == 0
        && snapshot->candidates[0].hardware_id_count == 1
        && std::strcmp(snapshot->candidates[0].hardware_ids[0], "USB\\VID_056A&PID_03FD") == 0);
    check("per-node driver retained", snapshot->container_device_count == 2 && std::strcmp(snapshot->container_devices[1].driver_version, "4.0.0.4") == 0);
    pen_wintab_free_windows_device(nullptr);
    std::printf("RESULT Windows metadata %d failed\n", failed);
    return failed ? 1 : 0;
}

int windows_device_info_live() {
    std::unique_ptr<const PenWintabInfo, decltype(&pen_wintab_free_info)> info(pen_wintab_query_info(), pen_wintab_free_info);
    if (!info || info->device_count < 1) return 1;
    for (int i = 0; i < info->device_count; ++i) {
        Info details(pen_wintab_query_windows_device(info->devices[i].plug_and_play_id), pen_wintab_free_windows_device);
        if (!details) return 1;
        std::fprintf(stderr, "Windows match status=%d candidates=%u container nodes=%u\n", details->status, details->candidate_count, details->container_device_count);
        if (details->status != PEN_WINDOWS_DEVICE_MATCHED_INSTANCE_ID && details->status != PEN_WINDOWS_DEVICE_MATCHED_USB_SERIAL) return 2;
        for (uint32_t n = 0; n < details->container_device_count; ++n) {
            const auto& device = details->container_devices[n];
            std::fprintf(stderr, "%s | USB=%04x:%04x rev=%04x | driver=%s %s\n",
                device.bus_reported_name ? device.bus_reported_name : device.friendly_name ? device.friendly_name : "unknown",
                device.usb_vendor_id, device.usb_product_id, device.usb_device_revision,
                device.driver_provider ? device.driver_provider : "unknown", device.driver_version ? device.driver_version : "unknown");
        }
    }
    return 0;
}
