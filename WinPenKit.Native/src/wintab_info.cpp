#include "wintab_info.h"
#include <algorithm>
#include <cstring>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace {
constexpr UINT maximum_bytes = 1024 * 1024;
constexpr UINT ifc_id = 1, ifc_spec = 2, ifc_impl = 3, ifc_devices = 4;
constexpr UINT dvc_name = 1, dvc_pnpid = 19;

struct Snapshot : PenWintabInfo {
    std::optional<std::string> id;
    std::vector<std::optional<std::string>> names, pnp_ids;
    std::vector<PenWintabDeviceInfo> entries;
    Snapshot() : PenWintabInfo{nullptr, -1, -1, -1, nullptr} {}
};

const char* pointer(const std::optional<std::string>& text) {
    return text ? text->c_str() : nullptr;
}

class Reader {
    WTINFOW_FUNC query_;
    UINT largest_;
public:
    Reader(WTINFOW_FUNC query, UINT largest) : query_(query), largest_(largest) {}

    std::optional<std::vector<unsigned char>> bytes(UINT category, UINT index) const {
        UINT required = query_(category, index, nullptr);
        if (!required || required > maximum_bytes) return std::nullopt;
        // WTInfo has no capacity parameter. A conforming driver must honor its size reports.
        std::vector<unsigned char> buffer(std::max(required, largest_), 0xff);
        UINT written = query_(category, index, buffer.data());
        if (!written || written > buffer.size()) return std::nullopt;
        buffer.resize(written);
        return buffer;
    }

    template<typename T> std::optional<T> scalar(UINT category, UINT index) const {
        auto buffer = bytes(category, index);
        if (!buffer || buffer->size() != sizeof(T)) return std::nullopt;
        T value;
        std::memcpy(&value, buffer->data(), sizeof(T));
        return value;
    }

    std::optional<std::string> text(UINT category, UINT index) const {
        auto buffer = bytes(category, index);
        if (!buffer || buffer->size() % sizeof(wchar_t)) return std::nullopt;
        for (size_t i = 0; i < buffer->size(); i += sizeof(wchar_t)) {
            if ((*buffer)[i] || (*buffer)[i + 1]) continue;
            if (i == 0) return std::string{};
            std::wstring wide(i / sizeof(wchar_t), L'\0');
            std::memcpy(wide.data(), buffer->data(), i);
            int size = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                nullptr, 0, nullptr, nullptr);
            if (!size) return std::nullopt;
            std::string result(size, '\0');
            if (!WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                result.data(), size, nullptr, nullptr)) return std::nullopt;
            return result;
        }
        return std::nullopt;
    }
};
}

namespace wintab {
const PenWintabInfo* read_info(WTINFOW_FUNC query) {
    if (!query) return nullptr;
    UINT largest = query(0, 0, nullptr);
    if (!largest || largest > maximum_bytes) return nullptr;
    Reader reader(query, largest);
    auto result = std::make_unique<Snapshot>();
    result->id = reader.text(WTI_INTERFACE, ifc_id);
    result->identification = pointer(result->id);
    auto spec = reader.scalar<WORD>(WTI_INTERFACE, ifc_spec);
    auto impl = reader.scalar<WORD>(WTI_INTERFACE, ifc_impl);
    result->specification_version = spec ? *spec : -1;
    result->implementation_version = impl ? *impl : -1;
    auto count = reader.scalar<UINT>(WTI_INTERFACE, ifc_devices);
    // WTI_DEVICES occupies 100..199; do not walk into WTI_CURSORS for a corrupt count.
    if (count && *count <= 100) {
        result->device_count = static_cast<int32_t>(*count);
        result->names.resize(*count);
        result->pnp_ids.resize(*count);
        result->entries.resize(*count);
        for (UINT i = 0; i < *count; ++i) {
            result->names[i] = reader.text(WTI_DEVICES + i, dvc_name);
            result->pnp_ids[i] = reader.text(WTI_DEVICES + i, dvc_pnpid);
            result->entries[i] = {i, pointer(result->names[i]), pointer(result->pnp_ids[i])};
        }
        result->devices = *count ? result->entries.data() : nullptr;
    }
    return result.release();
}
}

extern "C" {
const PenWintabInfo* pen_wintab_query_info(void) {
    struct Module {
        HMODULE handle = LoadLibraryW(L"Wintab32.dll");
        ~Module() { if (handle) FreeLibrary(handle); }
    } module;
    if (!module.handle) return nullptr;
    auto query = reinterpret_cast<WTINFOW_FUNC>(GetProcAddress(module.handle, "WTInfoW"));
    // No C++ exception may cross the C ABI. Module and snapshot storage use RAII.
    try { return wintab::read_info(query); }
    catch (...) { return nullptr; }
}

void pen_wintab_free_info(const PenWintabInfo* info) {
    delete static_cast<const Snapshot*>(info);
}
}
