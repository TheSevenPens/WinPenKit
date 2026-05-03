#pragma once

#include <cstdio>
#include <cstdarg>
#include <mutex>
#include <string>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace wintab {

// File-based diagnostic logger. Thread-safe.
// Logs to %TEMP%\WintabSessionCpp.log.
// Same format as the C# WintabSession logger.

class Logger {
public:
    static std::string get_log_path() {
        char temp_path[MAX_PATH];
        GetTempPathA(MAX_PATH, temp_path);
        return std::string(temp_path) + "WintabSessionCpp.log";
    }

    static void log(const char* fmt, ...) {
        char buffer[2048];

        // Format the timestamp
        SYSTEMTIME st;
        GetLocalTime(&st);
        int prefix_len = snprintf(buffer, sizeof(buffer),
            "[%02d:%02d:%02d.%03d] ",
            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

        // Format the message
        va_list args;
        va_start(args, fmt);
        vsnprintf(buffer + prefix_len, sizeof(buffer) - prefix_len, fmt, args);
        va_end(args);

        // Write to debugger
        OutputDebugStringA(buffer);
        OutputDebugStringA("\n");

        // Write to file
        auto& state = get_state();
        std::lock_guard<std::mutex> lock(state.mutex);
        if (!state.file) {
            state.file = fopen(state.path.c_str(), "w");
            if (!state.file) return;
        }
        fprintf(state.file, "%s\n", buffer);
        fflush(state.file);
    }

    static void close() {
        auto& state = get_state();
        std::lock_guard<std::mutex> lock(state.mutex);
        if (state.file) {
            fclose(state.file);
            state.file = nullptr;
        }
    }

private:
    struct State {
        std::mutex mutex;
        FILE* file = nullptr;
        std::string path = get_log_path();
        ~State() {
            if (file) {
                fclose(file);
                file = nullptr;
            }
        }
    };

    static State& get_state() {
        static State s;
        return s;
    }
};

} // namespace wintab
