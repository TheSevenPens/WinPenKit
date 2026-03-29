#pragma once

// Dynamic loader for Wintab32.dll with RAII.
// Loads all required function pointers at construction.
// Automatically frees the DLL at destruction.

#include "wintab/wintab.h"
#include "log.h"

namespace wintab {

class WintabLoader {
public:
    // Function pointers — valid after successful load()
    WTINFOA_FUNC      wt_info       = nullptr;
    WTOPENA_FUNC      wt_open       = nullptr;
    WTCLOSE_FUNC      wt_close      = nullptr;
    WTENABLE_FUNC     wt_enable     = nullptr;
    WTGETA_FUNC       wt_get        = nullptr;
    WTPACKET_FUNC     wt_packet     = nullptr;
    WTPACKETSGET_FUNC wt_packets_get = nullptr;
    WTOVERLAP_FUNC    wt_overlap    = nullptr;

    WintabLoader() = default;
    ~WintabLoader() { unload(); }

    // Non-copyable, movable
    WintabLoader(const WintabLoader&) = delete;
    WintabLoader& operator=(const WintabLoader&) = delete;
    WintabLoader(WintabLoader&& other) noexcept : module_(other.module_) {
        wt_info        = other.wt_info;
        wt_open        = other.wt_open;
        wt_close       = other.wt_close;
        wt_enable      = other.wt_enable;
        wt_get         = other.wt_get;
        wt_packet      = other.wt_packet;
        wt_packets_get = other.wt_packets_get;
        wt_overlap     = other.wt_overlap;
        other.module_  = nullptr;
    }

    bool load() {
        if (module_) return true;

        module_ = LoadLibraryW(L"Wintab32.dll");
        if (!module_) {
            Logger::log("Failed to load Wintab32.dll");
            return false;
        }

        wt_info        = get_proc<WTINFOA_FUNC>("WTInfoA");
        wt_open        = get_proc<WTOPENA_FUNC>("WTOpenA");
        wt_close       = get_proc<WTCLOSE_FUNC>("WTClose");
        wt_enable      = get_proc<WTENABLE_FUNC>("WTEnable");
        wt_get         = get_proc<WTGETA_FUNC>("WTGetA");
        wt_packet      = get_proc<WTPACKET_FUNC>("WTPacket");
        wt_packets_get = get_proc<WTPACKETSGET_FUNC>("WTPacketsGet");
        wt_overlap     = get_proc<WTOVERLAP_FUNC>("WTOverlap");

        bool ok = wt_info && wt_open && wt_close && wt_enable &&
                  wt_get && wt_packet && wt_packets_get && wt_overlap;

        if (!ok) {
            Logger::log("Missing required exports from Wintab32.dll");
            unload();
            return false;
        }

        Logger::log("Loaded Wintab32.dll successfully");
        return true;
    }

    void unload() {
        if (module_) {
            FreeLibrary(module_);
            module_ = nullptr;
        }
        wt_info = nullptr;
        wt_open = nullptr;
        wt_close = nullptr;
        wt_enable = nullptr;
        wt_get = nullptr;
        wt_packet = nullptr;
        wt_packets_get = nullptr;
        wt_overlap = nullptr;
    }

    bool is_loaded() const { return module_ != nullptr; }

private:
    HMODULE module_ = nullptr;

    template<typename T>
    T get_proc(const char* name) {
        auto proc = reinterpret_cast<T>(GetProcAddress(module_, name));
        if (!proc) Logger::log("Failed to get proc: %s", name);
        return proc;
    }
};

} // namespace wintab
