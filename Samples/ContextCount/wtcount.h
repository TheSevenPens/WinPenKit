/* wtcount.h -- the boilerplate the two context demos share.
 *
 * Wintab is loaded by name rather than linked, so these build with nothing but a compiler: no
 * Wacom SDK, no import library, no headers but this one. Only the handful of declarations the
 * demos actually use are here.
 *
 * See Docs/WINTAB-CONTEXT-LEAK.md for what the demos are for and what they showed.
 */

#ifndef WTCOUNT_H
#define WTCOUNT_H

#include <windows.h>
#include <stdio.h>

/* ---- the parts of Wintab these demos use ------------------------------------------------- */

#define WTI_INTERFACE   1u
#define WTI_STATUS      2u
#define WTI_DEFCONTEXT  3u   /* the driver's default DIGITISING context */
#define WTI_DEFSYSCTX   4u   /* the driver's default SYSTEM context */

#define IFC_NCONTEXTS   6u   /* "the number of contexts supported" */
#define STA_CONTEXTS    1u   /* "the number of contexts currently open" */
#define STA_SYSCTXS     2u   /* "the number of system contexts currently open" */

#define CXO_SYSTEM      0x0001u
#define CXO_MESSAGES    0x0004u

#define PK_ALL          0x1FFFu

typedef struct {
    char  lcName[40];
    UINT  lcOptions, lcStatus, lcLocks, lcMsgBase, lcDevice, lcPktRate;
    DWORD lcPktData, lcPktMode, lcMoveMask;
    DWORD lcBtnDnMask, lcBtnUpMask;
    LONG  lcInOrgX,  lcInOrgY,  lcInOrgZ;
    LONG  lcInExtX,  lcInExtY,  lcInExtZ;
    LONG  lcOutOrgX, lcOutOrgY, lcOutOrgZ;
    LONG  lcOutExtX, lcOutExtY, lcOutExtZ;
    DWORD lcSensX, lcSensY, lcSensZ;
    BOOL  lcSysMode;
    int   lcSysOrgX, lcSysOrgY, lcSysExtX, lcSysExtY;
    DWORD lcSysSensX, lcSysSensY;
} LOGCONTEXTA;

/* Wintab's own headers call this API_ENTRY. It is __stdcall on 32-bit and nothing on x64,
   which is what WINAPI already expands to on each. */
typedef UINT   (WINAPI *WTINFOA)(UINT, UINT, LPVOID);
typedef HANDLE (WINAPI *WTOPENA)(HWND, LOGCONTEXTA *, BOOL);
typedef BOOL   (WINAPI *WTCLOSE)(HANDLE);

static WTINFOA wtInfo;
static WTOPENA wtOpen;
static WTCLOSE wtClose;

/* ---- loading, counting, and somewhere to hang a context on ------------------------------- */

static int wtLoad(void)
{
    HMODULE dll = LoadLibraryA("Wintab32.dll");
    if (!dll) { printf("Wintab32.dll is not installed.\n"); return 0; }

    wtInfo  = (WTINFOA)GetProcAddress(dll, "WTInfoA");
    wtOpen  = (WTOPENA)GetProcAddress(dll, "WTOpenA");
    wtClose = (WTCLOSE)GetProcAddress(dll, "WTClose");

    if (wtInfo && wtOpen && wtClose) return 1;

    printf("Wintab32.dll is missing one of WTInfoA, WTOpenA, WTClose.\n");
    return 0;
}

static UINT wtNumber(UINT category, UINT index)
{
    UINT value = 0;
    return wtInfo(category, index, &value) ? value : 0;
}

/* The two counters, printed the same way at every step so the steps can be compared. */
static void report(const char *when)
{
    printf("  %-22s  open %3u   system %3u   (driver says it supports %u)\n",
           when,
           wtNumber(WTI_STATUS, STA_CONTEXTS),
           wtNumber(WTI_STATUS, STA_SYSCTXS),
           wtNumber(WTI_INTERFACE, IFC_NCONTEXTS));
}

/* A context is opened against a window, so there has to be one. Nothing is drawn in it and no
   messages are pumped: these demos are about opening and closing, not about pen input. */
static HWND wtWindow(const char *title)
{
    return CreateWindowExA(0, "STATIC", title, WS_OVERLAPPED,
                           40, 40, 400, 200, NULL, NULL, GetModuleHandleA(NULL), NULL);
}

/* Every packet field, which is what a paint application asks for. Requesting data the driver
   does not have is one of the documented ways to make WTOpen fail, so it is worth saying out
   loud that these demos ask for everything. */
static void wantEverything(LOGCONTEXTA *lc)
{
    lc->lcPktData   = PK_ALL;
    lc->lcMoveMask  = PK_ALL;
    lc->lcBtnDnMask = 0xFFFFFFFFu;
    lc->lcBtnUpMask = 0xFFFFFFFFu;
}

#endif /* WTCOUNT_H */
