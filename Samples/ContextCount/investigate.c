/* Independent Wintab probe for issue #121. Does not use wtcount.h or WinPenKit.
 * Build in a VS developer prompt: cl /nologo /W4 /WX /wd4191 investigate.c user32.lib
 * Read README.md before using the deliberately leaking lifecycle modes.
 * No foreign contexts are closed, no service/settings are changed.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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
static DWORD expectedPid;

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
    printf("%04u-%02u-%02uT%02u:%02u:%02u.%03uZ,%lu,%s,%p,%lld,%.3f,%u,%u,%u,%u,%s\n",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
        GetCurrentProcessId(), event, handle, result, elapsed,
        total, totalBytes, system, systemBytes, detail);
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
    sprintf_s(prefix, sizeof prefix, "issue121-%lu-", expectedPid);
    if (expectedPid && valid && !strncmp(c.name, prefix, strlen(prefix)) && candidateCount < 256)
        candidates[candidateCount++] = context;
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
        sprintf_s(event, sizeof event, "device_%u", i);
        sprintf_s(detail, sizeof detail, "name=%.250s", name);
        row(event, NULL, size, 0, detail);
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
        if (i == 1) { ShowWindow(hwnd, SW_SHOWNOACTIVATE); pump(100); }
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

static int lifecycle(int count, const char *mode, const char *kind, int device, DWORD hold)
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
        sprintf_s(c.name, sizeof c.name, "issue121-%lu-%d", GetCurrentProcessId(), i);
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
static int reclaim(const char *mode, const char *kind, int count)
{
    char exe[MAX_PATH], command[1024];
    STARTUPINFOA startup = {0};
    PROCESS_INFORMATION process = {0};
    MgrOpenFn mgrOpen = (MgrOpenFn)GetProcAddress(dll, "WTMgrOpen");
    CloseFn mgrClose = (CloseFn)GetProcAddress(dll, "WTMgrClose");
    EnumFn enumerate = (EnumFn)GetProcAddress(dll, "WTMgrContextEnum");
    HWND hwnd = window();
    HANDLE manager = NULL, sentinel = NULL;
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
    strcpy_s(c.name, sizeof c.name, "issue121-live-sentinel");
    sentinel = openContext(hwnd, &c, TRUE);
    row("sentinel_open", sentinel, sentinel != NULL, 0, "must_survive");
    if (!sentinel) { failed = 1; goto done; }
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    sprintf_s(command, sizeof command, "\"%s\" lifecycle %d %s %s", exe, count, mode, kind);
    startup.cb = sizeof startup;
    if (!CreateProcessA(exe, command, NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process)) {
        row("child_create_failed", NULL, GetLastError(), 0, ""); failed = 1; goto done;
    }
    CloseHandle(process.hThread);
    waitResult = WaitForSingleObject(process.hProcess, 30000);
    row("child_wait", process.hProcess, waitResult, 0, "requires_WAIT_OBJECT_0");
    if (waitResult != WAIT_OBJECT_0) {
        /* Keep the child alive; do not force a new leak if an API is wedged. */
        CloseHandle(process.hProcess); failed = 1; goto done;
    }
    GetExitCodeProcess(process.hProcess, &exitCode);
    expectedPid = process.dwProcessId;
    row("child_exited", NULL, expectedPid, 0, exitCode ? "nonzero_exit" : "exit=0");
    candidateCount = 0;
    enumerated = 0;
    if (!enumerate(manager, visit, (LPARAM)manager)) failed = 1;
    row("reclaim_candidates", NULL, candidateCount, 0, "matches_confirmed_exited_child_name");
    for (i = 0; i < candidateCount; ++i) {
        Context check = {0};
        char prefix[48];
        BOOL ok;
        sprintf_s(prefix, sizeof prefix, "issue121-%lu-", expectedPid);
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
    enumerate(manager, visit, (LPARAM)manager);
done:
    if (sentinel) row("sentinel_close", sentinel, closeContext(sentinel), 0, "");
    if (manager) row("manager_close", manager, mgrClose(manager), 0, "reclaim");
    DestroyWindow(hwnd);
    row("reclaim_after", NULL, !failed, 0, "");
    return failed;
}

int main(int argc, char **argv)
{
    int result = 0;
    QueryPerformanceFrequency(&frequency);
    dll = LoadLibraryExA("Wintab32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) { fprintf(stderr, "Wintab load failed: %lu\n", GetLastError()); return 2; }
    info = (InfoFn)GetProcAddress(dll, "WTInfoA");
    openContext = (OpenFn)GetProcAddress(dll, "WTOpenA");
    closeContext = (CloseFn)GetProcAddress(dll, "WTClose");
    getContext = (GetFn)GetProcAddress(dll, "WTGetA");
    if (!info || !openContext || !closeContext) return 2;
    puts("utc,pid,event,handle,result,elapsed_ms,contexts,contexts_bytes,system,system_bytes,detail");
    if (argc == 1 || strcmp(argv[1], "snapshot") == 0) row("snapshot", NULL, 0, 0, "");
    else if (strcmp(argv[1], "info") == 0) metadata();
    else if (strcmp(argv[1], "manager") == 0) result = managerProbe();
    else if (strcmp(argv[1], "lifecycle") == 0 && argc >= 5) {
        int count = atoi(argv[2]);
        const char *mode = argv[3], *kind = argv[4];
        if (count < 1 || count > 128 ||
            (strcmp(mode, "clean") && strcmp(mode, "kill") && strcmp(mode, "return") &&
             strcmp(mode, "destroy-return") && strcmp(mode, "cleanup")) ||
            (strcmp(kind, "system") && strcmp(kind, "digitizer") && strcmp(kind, "mixed") && strcmp(kind, "null") && strcmp(kind, "null-poll"))) return 2;
        result = lifecycle(count, mode, kind, argc > 5 ? atoi(argv[5]) : -1,
            argc > 6 ? (DWORD)strtoul(argv[6], NULL, 10) : 0);
        /* return modes deliberately retain the DLL until process shutdown. */
        if (!strcmp(mode, "return") || !strcmp(mode, "destroy-return")) return result;
    }
    else if (strcmp(argv[1], "reclaim") == 0 && argc == 5) {
        int count = atoi(argv[4]);
        if (count < 1 || count > 32 ||
            (strcmp(argv[2], "kill") && strcmp(argv[2], "return") && strcmp(argv[2], "destroy-return")) ||
            (strcmp(argv[3], "system") && strcmp(argv[3], "digitizer"))) return 2;
        result = reclaim(argv[2], argv[3], count);
    }
    else if (strcmp(argv[1], "watch") == 0 && argc == 4) {
        ULONGLONG end = GetTickCount64() + (ULONGLONG)strtoul(argv[2], NULL, 10) * 1000;
        DWORD interval = (DWORD)strtoul(argv[3], NULL, 10);
        if (!interval) return 2;
        do { row("sample", NULL, 0, 0, "persistent_DLL"); pump(interval); }
        while (GetTickCount64() < end);
        row("sample_end", NULL, 0, 0, "persistent_DLL");
    }
    else { fprintf(stderr, "Usage: investigate [snapshot|info|manager|lifecycle N clean|cleanup|kill|return|destroy-return system|digitizer|mixed|null [device|-1] [hold_ms]|watch seconds interval_ms]\n"); result = 2; }
    FreeLibrary(dll);
    return result;
}
