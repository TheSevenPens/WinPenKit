/* Independent Wintab probe for issue #121. Does not use wtcount.h or WinPenKit.
 * Build in a VS developer prompt: cl /nologo /W4 /WX /wd4191 investigate.c user32.lib
 * Read README.md before using the deliberately leaking lifecycle modes.
 * Foreign closes are restricted to the probe's own identified, exited child.
 * No service/settings are changed.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <limits.h>

typedef struct {
    char name[40];
    UINT options, status, locks, msgBase, device, pktRate;
    DWORD pktData, pktMode, moveMask, btnDnMask, btnUpMask;
    LONG inOrgX, inOrgY, inOrgZ, inExtX, inExtY, inExtZ;
    LONG outOrgX, outOrgY, outOrgZ, outExtX, outExtY, outExtZ;
    DWORD sensX, sensY, sensZ;
    BOOL sysMode;
    int sysOrgX, sysOrgY, sysExtX, sysExtY;
    DWORD sysSensX, sysSensY;
} Context;
typedef UINT (WINAPI *InfoFn)(UINT, UINT, void *);
typedef HANDLE (WINAPI *OpenFn)(HWND, Context *, BOOL);
typedef BOOL (WINAPI *CloseFn)(HANDLE);
typedef BOOL (WINAPI *GetFn)(HANDLE, Context *);
typedef HANDLE (WINAPI *MgrOpenFn)(HWND, UINT);
typedef BOOL (WINAPI *EnumCallback)(HANDLE, LPARAM);
typedef BOOL (WINAPI *EnumFn)(HANDLE, EnumCallback, LPARAM);
typedef HWND (WINAPI *OwnerFn)(HANDLE, HANDLE);
typedef HANDLE (WINAPI *DefFn)(HANDLE, BOOL);
typedef BOOL (WINAPI *CleanupFn)(void);

static HMODULE dll;
static InfoFn info;
static OpenFn openContext;
static CloseFn closeContext;
static GetFn getContext;
static OwnerFn owner;
static LARGE_INTEGER frequency;
static unsigned enumerated;
static HANDLE candidates[256];
static unsigned candidateCount;
static BOOL candidateOverflow;
static DWORD expectedPid;
static char expectedTag[17];

static long number(const char *text, long minimum, long maximum)
{
    char *end;
    long value;
    errno = 0;
    value = strtol(text, &end, 10);
    return errno || !*text || *end || value < minimum || value > maximum ? LONG_MIN : value;
}

static double now(void)
{
    LARGE_INTEGER tick;
    QueryPerformanceCounter(&tick);
    return 1000.0 * (double)tick.QuadPart / (double)frequency.QuadPart;
}

static void row(const char *event, HANDLE handle, long long result,
                double elapsed, const char *detail)
{
    SYSTEMTIME t;
    UINT total = 0xFFFFFFFFu, system = 0xFFFFFFFFu;
    /* Direct independent measurement, with return sizes retained. Failed reads
     * stay distinguishable from a reported zero. No existing wrapper involved. */
    UINT totalBytes = info(2, 1, &total);
    UINT systemBytes = info(2, 2, &system);
    GetSystemTime(&t);
    printf("%04u-%02u-%02uT%02u:%02u:%02u.%03uZ,%lu,%s,%p,%lld,%.3f,%u,%u,%u,%u,\"",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
        GetCurrentProcessId(), event, handle, result, elapsed,
        total, totalBytes, system, systemBytes);
    /* Context names are external text, so quote/escape the CSV field. */
    while (*detail) {
        if (*detail == '"') putchar('"');
        putchar(*detail++);
    }
    puts("\"");
    fflush(stdout);
}

static HWND window(void)
{
    return CreateWindowExA(0, "STATIC", "WinPenKit issue 121 probe",
        WS_OVERLAPPEDWINDOW, 40, 40, 400, 200, NULL, NULL, GetModuleHandleA(NULL), NULL);
}

static void pump(DWORD milliseconds)
{
    ULONGLONG until = GetTickCount64() + milliseconds;
    do {
        MSG message;
        while (PeekMessageA(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageA(&message);
        }
        if (GetTickCount64() >= until) break;
        Sleep(10);
    } while (1);
}

static BOOL WINAPI visit(HANDLE context, LPARAM manager)
{
    DWORD pid = 0;
    HWND hwnd = owner ? owner((HANDLE)manager, context) : NULL;
    Context c = {0};
    char detail[256], prefix[48];
    BOOL valid = getContext ? getContext(context, &c) : FALSE;
    GetWindowThreadProcessId(hwnd, &pid);
    c.name[sizeof c.name - 1] = 0;
    sprintf_s(detail, sizeof detail, "owner=%p;isWindow=%d;ownerPid=%lu;get=%d;device=%u;options=0x%X;name=%s",
        hwnd, IsWindow(hwnd), pid, valid, c.device, c.options, c.name);
    row("enumerated_context", context, pid, 0, detail);
    sprintf_s(prefix, sizeof prefix, "i121-%lu-%s-", expectedPid, expectedTag);
    if (expectedPid && valid && !strncmp(c.name, prefix, strlen(prefix))) {
        if (candidateCount < 256) candidates[candidateCount++] = context;
        else candidateOverflow = TRUE;
    }
    ++enumerated;
    return TRUE;
}

static void metadata(void)
{
    const UINT categories[] = {1, 2};
    unsigned k, i;
    char path[MAX_PATH] = {0};
    GetModuleFileNameA(dll, path, MAX_PATH);
    row("dll", dll, sizeof(void *) * 8, 0, path);
    row("coordinator_module_loaded", GetModuleHandleA("Wintab32.dll"),
        GetModuleHandleA("Wintab32.dll") != NULL, 0, "after_first_WTInfo_queries");
    row("context_struct_bytes", NULL, sizeof(Context), 0, "expected=172");
    for (k = 0; k < 2; ++k) {
        for (i = 1; i <= (k ? 8u : 10u); ++i) {
            union { BYTE bytes[2048]; UINT number; WORD word; } value = {0};
            char event[40], detail[80];
            UINT size = info(categories[k], i, &value);
            sprintf_s(event, sizeof event, "info_%u_%u", categories[k], i);
            if (k == 0 && i == 1) sprintf_s(detail, sizeof detail, "bytes=%u;name=%.60s", size, (char *)value.bytes);
            else sprintf_s(detail, sizeof detail, "bytes=%u", size);
            row(event, NULL, size ? value.number : -1LL, 0, detail);
        }
    }
    for (i = 0; i < 3; ++i) {
        char name[256] = {0}, event[40], detail[320];
        UINT size = info(100 + i, 1, name);
        UINT index;
        sprintf_s(event, sizeof event, "device_%u", i);
        sprintf_s(detail, sizeof detail, "name=%.250s", name);
        row(event, NULL, size, 0, detail);
        if (!size || !name[0]) continue;
        memset(name, 0, sizeof name);
        size = info(100 + i, 19, name); /* DVC_PNPID can contain a hardware serial. */
        sprintf_s(event, sizeof event, "device_%u_pnpid", i);
        row(event, NULL, size, 0, name[0] ? "nonempty_identifier_not_recorded" : "empty_identifier");
        for (index = 2; index <= 18; ++index) {
            typedef struct { LONG minimum, maximum; UINT units; DWORD resolution; } Axis;
            union { UINT number; Axis axes[3]; } value = {0};
            UINT expected = index <= 11 ? 4u : index <= 16 ? 16u : 48u;
            size = info(100 + i, index, &value);
            sprintf_s(event, sizeof event, "device_%u_index_%u", i, index);
            if (size != expected) {
                sprintf_s(detail, sizeof detail, "bytes=%u;expected=%u;unavailable", size, expected);
                row(event, NULL, -1, 0, detail);
            } else if (index <= 11) {
                row(event, NULL, value.number, 0, "bytes=4");
            } else {
                unsigned axis, length = 0;
                for (axis = 0; axis < size / sizeof(Axis); ++axis) {
                    Axis a = value.axes[axis];
                    length += (unsigned)sprintf_s(detail + length, sizeof detail - length,
                        "axis%u=min:%ld/max:%ld/units:%u/resolution:%lu;",
                        axis, a.minimum, a.maximum, a.units, a.resolution);
                }
                row(event, NULL, size, 0, detail);
            }
        }
    }
    {
        UINT count = 0;
        if (info(1, 5, &count) != 4) return;
        for (i = 0; i < count && i < 32; ++i) {
            char name[256] = {0}, event[40], detail[320];
            UINT active = 0xFFFFFFFFu;
            UINT nameBytes = info(200 + i, 1, name);
            UINT activeBytes = info(200 + i, 2, &active);
            sprintf_s(event, sizeof event, "cursor_%u_active", i);
            sprintf_s(detail, sizeof detail, "name=%.250s;name_bytes=%u;active_bytes=%u", name, nameBytes, activeBytes);
            row(event, NULL, activeBytes == 4 ? active : -1LL, 0, detail);
        }
    }
}

static int managerProbe(void)
{
    MgrOpenFn mgrOpen = (MgrOpenFn)GetProcAddress(dll, "WTMgrOpen");
    CloseFn mgrClose = (CloseFn)GetProcAddress(dll, "WTMgrClose");
    EnumFn enumerate = (EnumFn)GetProcAddress(dll, "WTMgrContextEnum");
    DefFn def = (DefFn)GetProcAddress(dll, "WTMgrDefContext");
    HWND hwnd = window();
    unsigned i;
    if (!hwnd) return 2;
    owner = (OwnerFn)GetProcAddress(dll, "WTMgrContextOwner");
    row("manager_exports", NULL, mgrOpen && mgrClose && enumerate && owner && def,
        0, "open;close;enum;owner;default");
    if (!mgrOpen || !mgrClose) { DestroyWindow(hwnd); return 2; }
    for (i = 0; i < 3; ++i) {
        HANDLE manager;
        DWORD error;
        double start, elapsed;
        const char *kind = i == 0 ? "hidden" : i == 1 ? "visible" : "null_hwnd";
        char detail[100];
        if (i == 1) {
            /* STARTUPINFO can override the first ShowWindow when the console
             * was launched hidden. Explicitly set visibility without activation. */
            SetWindowPos(hwnd, NULL, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            pump(100);
        }
        row("manager_window_visible", i == 2 ? NULL : hwnd,
            i == 2 ? FALSE : IsWindowVisible(hwnd), 0, kind);
        SetLastError(0);
        start = now();
        manager = mgrOpen(i == 2 ? NULL : hwnd, 0x7FF0);
        elapsed = now() - start;
        error = GetLastError();
        sprintf_s(detail, sizeof detail, "%s;lastError=%lu", kind, error);
        row("manager_open", manager, manager != NULL, elapsed, detail);
        if (manager) {
            if (enumerate) {
                BOOL ok;
                enumerated = 0;
                ok = enumerate(manager, visit, (LPARAM)manager);
                sprintf_s(detail, sizeof detail, "callbacks=%u", enumerated);
                row("manager_enum", manager, ok, 0, detail);
            }
            if (def) {
                row("manager_default_digitizer", def(manager, FALSE), 0, 0, "read_only");
                row("manager_default_system", def(manager, TRUE), 0, 0, "read_only");
            }
            row("manager_close", manager, mgrClose(manager), 0, kind);
        }
    }
    DestroyWindow(hwnd);
    return 0;
}

static int lifecycle(int count, const char *mode, const char *kind, int device, DWORD hold, const char *tag)
{
    HANDLE contexts[128] = {0};
    HWND hwnd = window();
    int i, opened = 0, failed = 0;
    BOOL nullWindow = strncmp(kind, "null", 4) == 0;
    if (!hwnd) return 2;
    row("before", NULL, count, 0, mode);
    for (i = 0; i < count; ++i) {
        Context c = {0};
        UINT bytes = info(4, 0, &c);
        BOOL system = strcmp(kind, "digitizer") != 0 && (strcmp(kind, "mixed") != 0 || i % 2 == 0);
        char detail[160];
        double start, elapsed;
        if (bytes != sizeof c) { row("defaults_failed", NULL, bytes, 0, ""); failed = 1; break; }
        if (system) c.options |= 1u; else c.options &= ~1u;
        c.options |= 4u;
        if (strcmp(kind, "null-poll") == 0) c.options &= ~4u;
        if (device >= 0) c.device = (UINT)device;
        c.pktData = c.moveMask = 0x1FFFu;
        c.btnDnMask = c.btnUpMask = 0xFFFFFFFFu;
        if (tag) sprintf_s(c.name, sizeof c.name, "i121-%lu-%s-%d", GetCurrentProcessId(), tag, i);
        else sprintf_s(c.name, sizeof c.name, "issue121-%lu-%d", GetCurrentProcessId(), i);
        start = now();
        contexts[i] = openContext(nullWindow ? NULL : hwnd, &c, TRUE);
        elapsed = now() - start;
        sprintf_s(detail, sizeof detail, "index=%d;device=%u;options=0x%X;hwnd=%p",
            i, c.device, c.options, nullWindow ? NULL : hwnd);
        row("open", contexts[i], contexts[i] != NULL, elapsed, detail);
        if (!contexts[i]) { failed = 1; break; }
        ++opened;
    }
    pump(hold);
    if (!failed && strcmp(mode, "kill") == 0) {
        row("terminate_self", NULL, opened, 0, "no_WTClose");
        TerminateProcess(GetCurrentProcess(), 0);
    }
    if (!failed && strcmp(mode, "return") == 0) {
        row("return_without_close", NULL, opened, 0, "CRT_shutdown");
        return 0;
    }
    if (!failed && strcmp(mode, "destroy-return") == 0) {
        BOOL ok = DestroyWindow(hwnd);
        pump(500);
        row("destroy_window", hwnd, ok, 0, "no_WTClose;500ms_pumped");
        return 0;
    }
    for (i = opened - 1; i >= 0; --i) {
        BOOL ok;
        double start = now(), elapsed;
        ok = closeContext(contexts[i]);
        elapsed = now() - start;
        row("close", contexts[i], ok, elapsed, "");
        if (!ok) failed = 1;
    }
    if (strcmp(mode, "cleanup") == 0) {
        CleanupFn cleanup = (CleanupFn)GetProcAddress(dll, "WacomCleanup");
        row("cleanup_export", NULL, cleanup != NULL, 0, "");
        if (cleanup) {
            BOOL ok = cleanup();
            row("wacom_cleanup", NULL, ok, 0, "all_own_contexts_closed");
        }
    }
    DestroyWindow(hwnd);
    row("after", NULL, opened, 0, mode);
    return failed;
}

/* The sole foreign-close experiment. The parent launches its own child, keeps
 * the Windows process handle, waits for confirmed exit, and only accepts context
 * names belonging to that child. NULL/invalid HWND alone is NEVER a deletion rule.
 * The sentinel is this parent's live context and must survive foreign closes.
 */
static int reclaim(const char *mode, const char *kind, int count, HANDLE existingSentinel)
{
    char exe[MAX_PATH], command[1024];
    LARGE_INTEGER nonce;
    STARTUPINFOA startup = {0};
    PROCESS_INFORMATION process = {0};
    MgrOpenFn mgrOpen = (MgrOpenFn)GetProcAddress(dll, "WTMgrOpen");
    CloseFn mgrClose = (CloseFn)GetProcAddress(dll, "WTMgrClose");
    EnumFn enumerate = (EnumFn)GetProcAddress(dll, "WTMgrContextEnum");
    HWND hwnd = window();
    HANDLE manager = NULL, sentinel = existingSentinel;
    Context c = {0};
    unsigned i;
    int failed = 0;
    DWORD exitCode, waitResult;
    owner = (OwnerFn)GetProcAddress(dll, "WTMgrContextOwner");
    if (!hwnd || !mgrOpen || !mgrClose || !enumerate || !owner || !getContext) {
        if (hwnd) DestroyWindow(hwnd);
        return 2;
    }
    row("reclaim_before", NULL, count, 0, mode);
    manager = mgrOpen(hwnd, 0x7FF0);
    if (!manager || info(4, 0, &c) != sizeof c) { failed = 1; goto done; }
    if (!sentinel) {
        strcpy_s(c.name, sizeof c.name, "issue121-live-sentinel");
        sentinel = openContext(hwnd, &c, TRUE);
    }
    row("sentinel_open", sentinel, sentinel != NULL, 0, "must_survive");
    if (!sentinel) { failed = 1; goto done; }
    if (!GetModuleFileNameA(NULL, exe, MAX_PATH)) { failed = 1; goto done; }
    QueryPerformanceCounter(&nonce);
    sprintf_s(expectedTag, sizeof expectedTag, "%016llX", (unsigned long long)nonce.QuadPart);
    sprintf_s(command, sizeof command, "\"%s\" lifecycle %d %s %s -1 0 %s", exe, count, mode, kind, expectedTag);
    startup.cb = sizeof startup;
    if (!CreateProcessA(exe, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process)) {
        row("child_create_failed", NULL, GetLastError(), 0, ""); failed = 1; goto done;
    }
    CloseHandle(process.hThread);
    waitResult = WaitForSingleObject(process.hProcess, 30000);
    row("child_wait", process.hProcess, waitResult, 0, "requires_WAIT_OBJECT_0");
    if (waitResult != WAIT_OBJECT_0) {
        /* Keep the child alive; do not force a new leak if an API is wedged. */
        CloseHandle(process.hProcess); failed = 1; goto done;
    }
    if (!GetExitCodeProcess(process.hProcess, &exitCode)) {
        CloseHandle(process.hProcess); failed = 1; goto done;
    }
    if (exitCode != 0) failed = 1;
    expectedPid = process.dwProcessId;
    row("child_exited", NULL, expectedPid, 0, exitCode ? "nonzero_exit" : "exit=0");
    candidateCount = 0;
    candidateOverflow = FALSE;
    enumerated = 0;
    if (!enumerate(manager, visit, (LPARAM)manager)) failed = 1;
    row("reclaim_candidates", NULL, candidateCount, 0, "matches_confirmed_exited_child_name");
    if (!candidateCount || candidateOverflow) {
        row("reclaim_incomplete", NULL, 0, 0, candidateOverflow ? "candidate_capacity_exceeded" : "no_reclamation_demonstrated");
        failed = 1;
    }
    for (i = 0; i < candidateCount; ++i) {
        Context check = {0};
        char prefix[48];
        BOOL ok;
        sprintf_s(prefix, sizeof prefix, "i121-%lu-%s-", expectedPid, expectedTag);
        if (!getContext(candidates[i], &check) || strncmp(check.name, prefix, strlen(prefix))) {
            row("foreign_close_skipped", candidates[i], 0, 0, "identity_changed"); failed = 1; continue;
        }
        ok = closeContext(candidates[i]);
        row("foreign_close", candidates[i], ok, 0, "known_child_only");
        if (!ok) failed = 1;
    }
    expectedPid = 0;
    CloseHandle(process.hProcess);
    {
        BOOL valid = getContext(sentinel, &c);
        row("sentinel_still_valid", sentinel, valid, 0, "WTGetA");
        if (!valid) failed = 1;
    }
    if (!enumerate(manager, visit, (LPARAM)manager)) failed = 1;
done:
    if (sentinel && !existingSentinel) {
        BOOL ok = closeContext(sentinel);
        row("sentinel_close", sentinel, ok, 0, "");
        if (!ok) failed = 1;
    }
    if (manager) {
        BOOL ok = mgrClose(manager);
        row("manager_close", manager, ok, 0, "reclaim");
        if (!ok) failed = 1;
    }
    DestroyWindow(hwnd);
    row("reclaim_after", NULL, !failed, 0, "");
    return failed;
}

/* Manual hardware control: packets must arrive on the SAME context before and
 * after reclaiming a test child's contexts. Poll a fixed, checked packet layout;
 * mouse/WM_POINTER messages cannot satisfy this test. No pen input is synthesized.
 */
static int packetCheck(BOOL reclaimChild, int x, int y)
{
    typedef struct { UINT serial, cursor; LONG x, y; UINT pressure; } Packet;
    typedef int (WINAPI *PacketsFn)(HANDLE, int, void *);
    typedef int (WINAPI *QueueSizeFn)(HANDLE);
    PacketsFn packets = (PacketsFn)GetProcAddress(dll, "WTPacketsGet");
    QueueSizeFn queueSize = (QueueSizeFn)GetProcAddress(dll, "WTQueueSizeGet");
    HWND hwnd, label;
    HANDLE context = NULL;
    Context c = {0};
    ULONGLONG deadline;
    unsigned contactPackets = 0, phase = 0;
    int failed = 1;
    /* Keep optional desktop coordinates consistent across mixed-DPI monitors. */
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    hwnd = window();
    if (!hwnd) return 2;
    if (!packets || !queueSize || info(4, 0, &c) != sizeof c) goto done;
    c.options |= 1u;
    c.options &= ~4u;
    c.pktData = c.moveMask = 0x5B0u; /* SERIAL_NUMBER | CURSOR | X | Y | NORMAL_PRESSURE */
    c.pktMode = 0;
    strcpy_s(c.name, sizeof c.name, "issue121-packet-sentinel");
    context = openContext(hwnd, &c, TRUE);
    row("packet_context_open", context, context != NULL, 0, "physical_pen_required");
    if (!context || c.pktData != 0x5B0u || c.pktMode != 0) goto done;
    SetWindowTextA(hwnd, reclaimChild ? "Wintab packet check - draw continuously for a few seconds" :
        "Wintab input-only check - draw a short stroke");
    label = CreateWindowExA(0, "STATIC",
        reclaimChild ? "Draw on the tablet while this window is active.\nKeep drawing when the text changes.\nNo ink is drawn; this checks pressure packets.\nThe window closes when both phases pass." :
        "Draw a short stroke here.\nNo ink is drawn; this checks pressure packets.\nNo child contexts are opened or reclaimed.\nThe window closes when input is verified.",
        WS_CHILD | WS_VISIBLE | SS_CENTER, 10, 20, 360, 120, hwnd, NULL, GetModuleHandleA(NULL), NULL);
    ShowWindow(hwnd, SW_RESTORE);
    /* The first ShowWindow can be overridden by the launcher's STARTUPINFO. */
    if (IsIconic(hwnd) || !IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_RESTORE);
    SetWindowPos(hwnd, HWND_TOP, x, y, 900, 600, SWP_SHOWWINDOW);
    row("packet_window_visible", hwnd, IsWindowVisible(hwnd) && !IsIconic(hwnd), 0, "visible_and_not_minimized");
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) goto done;
    SetForegroundWindow(hwnd);
    deadline = GetTickCount64() + 180000;
    while (IsWindow(hwnd) && GetTickCount64() < deadline) {
        Packet data[32];
        int n, i;
        pump(10);
        n = packets(context, 32, data);
        if (n < 0 || n > 32) break;
        for (i = 0; i < n; ++i) {
            char detail[128];
            sprintf_s(detail, sizeof detail, "phase=%u;serial=%u;cursor=%u;x=%ld;y=%ld;pressure=%u",
                phase, data[i].serial, data[i].cursor, data[i].x, data[i].y, data[i].pressure);
            row("hardware_packet", context, data[i].pressure, 0, detail);
            if (data[i].pressure) ++contactPackets;
        }
        if (contactPackets >= 16) {
            row("packet_phase_pass", context, contactPackets, 0,
                !reclaimChild ? "input_only" : phase ? "after_reclaim" : "before_reclaim");
            if (!reclaimChild || phase) { failed = 0; break; }
            SetWindowTextA(label, "First phase passed. Reclaiming test contexts...\nKeep drawing for the second phase.");
            if (reclaim("kill", "system", 3, context) != 0) break;
            /* Discard queued pre-reclamation packets so they cannot count as
             * evidence of post-reclamation delivery. */
            {
                int capacity = queueSize(context), flushed;
                if (capacity <= 0) break;
                flushed = packets(context, capacity, NULL);
                row("packet_queue_flush", context, flushed, 0, "discard_all_pre_reclamation_packets");
                if (flushed < 0) break;
            }
            contactPackets = 0;
            phase = 1;
            SetWindowTextA(label, "Cleanup completed. Keep drawing.\nNow checking packets on the original context.");
        }
    }
    row("packet_check_result", context, !failed, 0, failed ? "timeout_or_incomplete" :
        reclaimChild ? "same_context_both_phases" : "input_only_no_reclamation");
done:
    if (context) {
        BOOL ok = closeContext(context);
        row("packet_context_close", context, ok, 0, "");
        if (!ok) failed = 1;
    }
    if (IsWindow(hwnd)) DestroyWindow(hwnd);
    return failed;
}

int main(int argc, char **argv)
{
    int result = 0;
    QueryPerformanceFrequency(&frequency);
    /* direct-info is a read-only diagnostic of this Wacom installation's
     * backend, not an alternative application API or a path for context opens. */
    dll = LoadLibraryExA(argc == 2 && !strcmp(argv[1], "direct-info") ?
        "Wacom_Tablet.dll" : "Wintab32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) { fprintf(stderr, "Wintab load failed: %lu\n", GetLastError()); return 2; }
    info = (InfoFn)GetProcAddress(dll, "WTInfoA");
    openContext = (OpenFn)GetProcAddress(dll, "WTOpenA");
    closeContext = (CloseFn)GetProcAddress(dll, "WTClose");
    getContext = (GetFn)GetProcAddress(dll, "WTGetA");
    if (!info || !openContext || !closeContext) return 2;
    puts("utc,pid,event,handle,result,elapsed_ms,contexts,contexts_bytes,system,system_bytes,detail");
    if (argc == 1 || strcmp(argv[1], "snapshot") == 0) row("snapshot", NULL, 0, 0, "");
    else if (strcmp(argv[1], "info") == 0 || (argc == 2 && strcmp(argv[1], "direct-info") == 0)) metadata();
    else if (strcmp(argv[1], "manager") == 0) result = managerProbe();
    else if ((!strcmp(argv[1], "packet-check") || !strcmp(argv[1], "packet-only")) && (argc == 2 || argc == 4)) {
        long x = argc == 4 ? number(argv[2], -32768, 32767) : 40;
        long y = argc == 4 ? number(argv[3], -32768, 32767) : 40;
        if (x == LONG_MIN || y == LONG_MIN) return 2;
        result = packetCheck(!strcmp(argv[1], "packet-check"), (int)x, (int)y);
    }
    else if (strcmp(argv[1], "lifecycle") == 0 && argc >= 5 && argc <= 8) {
        int count = (int)number(argv[2], 1, 128);
        long device = argc > 5 ? number(argv[5], -1, 15) : -1;
        long hold = argc > 6 ? number(argv[6], 0, 3600000) : 0;
        const char *mode = argv[3], *kind = argv[4];
        const char *tag = argc > 7 ? argv[7] : NULL;
        if (count < 1 || device == LONG_MIN || hold == LONG_MIN ||
            (tag && (strlen(tag) != 16 || strspn(tag, "0123456789ABCDEF") != 16)) ||
            (strcmp(mode, "clean") && strcmp(mode, "kill") && strcmp(mode, "return") &&
             strcmp(mode, "destroy-return") && strcmp(mode, "cleanup")) ||
            (strcmp(kind, "system") && strcmp(kind, "digitizer") && strcmp(kind, "mixed") && strcmp(kind, "null") && strcmp(kind, "null-poll"))) return 2;
        result = lifecycle(count, mode, kind, (int)device, (DWORD)hold, tag);
        /* return modes deliberately retain the DLL until process shutdown. */
        if (!strcmp(mode, "return") || !strcmp(mode, "destroy-return")) return result;
    }
    else if (strcmp(argv[1], "reclaim") == 0 && argc == 5) {
        int count = (int)number(argv[4], 1, 128);
        if (count < 1 ||
            (strcmp(argv[2], "kill") && strcmp(argv[2], "return") && strcmp(argv[2], "destroy-return")) ||
            (strcmp(argv[3], "system") && strcmp(argv[3], "digitizer"))) return 2;
        result = reclaim(argv[2], argv[3], count, NULL);
    }
    else if (strcmp(argv[1], "watch") == 0 && argc == 4) {
        long seconds = number(argv[2], 1, 7200);
        long interval = number(argv[3], 1, 60000);
        ULONGLONG end;
        if (seconds == LONG_MIN || interval == LONG_MIN) return 2;
        end = GetTickCount64() + (ULONGLONG)seconds * 1000;
        do { row("sample", NULL, 0, 0, "persistent_DLL"); pump((DWORD)interval); }
        while (GetTickCount64() < end);
        row("sample_end", NULL, 0, 0, "persistent_DLL");
    }
    else {
        fprintf(stderr, "Usage:\n  investigate snapshot|info|direct-info|manager\n"
            "  investigate packet-check|packet-only [desktop_x desktop_y]\n"
            "  investigate lifecycle N clean|cleanup|kill|return|destroy-return system|digitizer|mixed|null|null-poll [device|-1] [hold_ms] [run_tag]\n"
            "  investigate reclaim kill|return|destroy-return system|digitizer N\n"
            "  investigate watch seconds interval_ms\n");
        result = 2;
    }
    FreeLibrary(dll);
    return result;
}
