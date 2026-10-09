#include "wintab_info.h"
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace {
std::map<std::pair<UINT, UINT>, std::vector<unsigned char>> fields;
UINT APIENTRY query(UINT category, UINT index, LPVOID output) {
    if (!category) return 4096;
    auto it = fields.find({category, index});
    if (it == fields.end()) return 0;
    if (output) std::memcpy(output, it->second.data(), it->second.size());
    return static_cast<UINT>(it->second.size());
}
template<typename T> void scalar(UINT category, UINT index, T value) {
    auto& data = fields[{category, index}];
    data.resize(sizeof(T));
    std::memcpy(data.data(), &value, sizeof(T));
}
void text(UINT category, UINT index, const wchar_t* value) {
    auto& data = fields[{category, index}];
    data.resize((std::wcslen(value) + 1) * sizeof(wchar_t));
    std::memcpy(data.data(), value, data.size());
}
bool equal(const char* actual, const char* expected) {
    return actual && std::strcmp(actual, expected) == 0;
}
using Info = std::unique_ptr<const PenWintabInfo, decltype(&pen_wintab_free_info)>;
Info read() { return Info(wintab::read_info(query), pen_wintab_free_info); }
}

int main(int argc, char** argv) {
    if (argc > 1 && std::strcmp(argv[1], "--live") == 0) {
        std::puts("Querying installed Wintab driver...");
        std::fflush(stdout);
        Info info(pen_wintab_query_info(), pen_wintab_free_info);
        // The installed driver interferes with this probe's stdout; use stderr for the report.
        if (!info) { std::fputs("Wintab metadata unavailable\n", stderr); return 1; }
        std::fprintf(stderr, "spec=0x%x implementation=0x%x devices=%d\n",
            info->specification_version, info->implementation_version, info->device_count);
        for (int i = 0; i < info->device_count; ++i) {
            const auto& device = info->devices[i];
            std::fprintf(stderr, "device %u: %s; PnP ID %s\n", device.device_index,
                device.name ? device.name : "unavailable",
                device.plug_and_play_id ? "present" : "unavailable");
        }
        return 0;
    }
    int failed = 0;
    auto check = [&](const char* name, bool passed) {
        std::printf("[%s] %s\n", passed ? "PASS" : "FAIL", name);
        if (!passed) ++failed;
    };
    text(1, 1, L"Wacom \u30c6\u30b9\u30c8");
    scalar<WORD>(1, 2, 0x0104);
    scalar<WORD>(1, 3, 0x0610);
    scalar<UINT>(1, 4, 2);
    text(100, 1, L"Tablet A"); text(100, 19, L"RAW-PNP-A");
    text(101, 1, L"Tablet B");
    auto info = read();
    check("UTF-16 converted to UTF-8", equal(info->identification, "Wacom \xe3\x83\x86\xe3\x82\xb9\xe3\x83\x88"));
    check("packed WORD versions", info->specification_version == 0x0104 && info->implementation_version == 0x0610);
    check("all device indices and optional raw PnP IDs", info->device_count == 2
        && info->devices[1].device_index == 1 && equal(info->devices[1].name, "Tablet B")
        && !info->devices[1].plug_and_play_id && equal(info->devices[0].plug_and_play_id, "RAW-PNP-A"));
    { auto other = read(); }
    check("snapshot owns its strings", equal(info->devices[0].name, "Tablet A"));
    fields[{1, 2}] = {4}; fields.erase({1, 3});
    fields[{100, 1}] = {65, 0}; fields[{100, 19}] = {65, 0, 0};
    text(101, 1, L"");
    info = read();
    check("unavailable and malformed versions", info->specification_version == -1 && info->implementation_version == -1);
    check("malformed strings unavailable; empty preserved", !info->devices[0].name
        && !info->devices[0].plug_and_play_id && equal(info->devices[1].name, ""));
    scalar<UINT>(1, 4, 0); info = read();
    check("zero devices", info->device_count == 0 && !info->devices);
    fields.erase({1, 4}); info = read();
    check("missing count distinct from zero", info->device_count == -1 && !info->devices);
    scalar<UINT>(1, 4, UINT_MAX); info = read();
    check("invalid count rejected", info->device_count == -1 && !info->devices);
    check("missing export", !wintab::read_info(nullptr));
    auto absent = +[](UINT, UINT, LPVOID) -> UINT { return 0; };
    check("unresponsive driver", !wintab::read_info(absent));
    auto oversized = +[](UINT, UINT, LPVOID) -> UINT { return UINT_MAX; };
    check("oversized category rejected", !wintab::read_info(oversized));
    auto bad_field = +[](UINT c, UINT, LPVOID) -> UINT { return c ? UINT_MAX : 4096; };
    Info bad(wintab::read_info(bad_field), pen_wintab_free_info);
    check("oversized field rejected", bad && !bad->identification);
    auto disappeared = +[](UINT c, UINT, LPVOID p) -> UINT { return !c ? 4096 : p ? 0 : 4; };
    bad.reset(wintab::read_info(disappeared));
    check("driver disappears between size and data", bad && !bad->identification);
    auto over_return = +[](UINT c, UINT, LPVOID p) -> UINT { return !c ? 4096 : p ? 4097 : 4; };
    bad.reset(wintab::read_info(over_return));
    check("oversized return rejected", bad && !bad->identification);
    pen_wintab_free_info(nullptr);
    std::printf("RESULT %d failed\n", failed);
    return failed ? 1 : 0;
}
