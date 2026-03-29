// Minimal Wintab 1.4 definitions for WintabSession.
// Only the types, constants, and function signatures we actually use.
// Based on the Wintab 1.4 specification from Wacom.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

// ── Handle types ────────────────────────────────────────────────

typedef HANDLE HCTX;
typedef UINT   WTPKT;

// ── FIX32 fixed-point type ──────────────────────────────────────

typedef DWORD FIX32;

// ── Message base ────────────────────────────────────────────────

constexpr UINT WT_DEFBASE    = 0x7FF0;
constexpr UINT WT_PACKET     = WT_DEFBASE + 0;
constexpr UINT WT_CTXOPEN    = WT_DEFBASE + 1;
constexpr UINT WT_CTXCLOSE   = WT_DEFBASE + 2;
constexpr UINT WT_CTXUPDATE  = WT_DEFBASE + 3;
constexpr UINT WT_CTXOVERLAP = WT_DEFBASE + 4;
constexpr UINT WT_PROXIMITY  = WT_DEFBASE + 5;
constexpr UINT WT_INFOCHANGE = WT_DEFBASE + 6;
constexpr UINT WT_CSRCHANGE  = WT_DEFBASE + 7;
constexpr UINT WT_PACKETEXT  = WT_DEFBASE + 8;

// ── WTI category indices ────────────────────────────────────────

constexpr UINT WTI_INTERFACE  = 1;
constexpr UINT WTI_STATUS     = 2;
constexpr UINT WTI_DEFCONTEXT = 3;
constexpr UINT WTI_DEFSYSCTX  = 4;
constexpr UINT WTI_DEVICES    = 100;
constexpr UINT WTI_CURSORS    = 200;
constexpr UINT WTI_EXTENSIONS = 300;

// ── WTI_DEVICES sub-indices ─────────────────────────────────────

constexpr UINT DVC_NPRESSURE   = 15;
constexpr UINT DVC_ORIENTATION = 17;

// ── Context option flags ────────────────────────────────────────

constexpr UINT CXO_SYSTEM   = 0x0001;
constexpr UINT CXO_PEN      = 0x0002;
constexpr UINT CXO_MESSAGES = 0x0004;

// ── Packet status flags ─────────────────────────────────────────

constexpr UINT TPS_PROXIMITY = 0x0001;

// ── Packet data bits ────────────────────────────────────────────

constexpr WTPKT PK_CONTEXT          = 0x0001;
constexpr WTPKT PK_STATUS           = 0x0002;
constexpr WTPKT PK_TIME             = 0x0004;
constexpr WTPKT PK_CHANGED          = 0x0008;
constexpr WTPKT PK_SERIAL_NUMBER    = 0x0010;
constexpr WTPKT PK_CURSOR           = 0x0020;
constexpr WTPKT PK_BUTTONS          = 0x0040;
constexpr WTPKT PK_X                = 0x0080;
constexpr WTPKT PK_Y                = 0x0100;
constexpr WTPKT PK_Z                = 0x0200;
constexpr WTPKT PK_NORMAL_PRESSURE  = 0x0400;
constexpr WTPKT PK_TANGENT_PRESSURE = 0x0800;
constexpr WTPKT PK_ORIENTATION      = 0x1000;
constexpr WTPKT PK_PKTBITS_ALL      = 0x1FFF;

// ── LOGCONTEXT ──────────────────────────────────────────────────
// 40-field struct that configures a Wintab context.

constexpr int LCNAMELEN = 40;

#pragma pack(push, 4)

typedef struct tagLOGCONTEXTA {
    char    lcName[LCNAMELEN];
    UINT    lcOptions;
    UINT    lcStatus;
    UINT    lcLocks;
    UINT    lcMsgBase;
    UINT    lcDevice;
    UINT    lcPktRate;
    WTPKT   lcPktData;
    WTPKT   lcPktMode;
    WTPKT   lcMoveMask;
    DWORD   lcBtnDnMask;
    DWORD   lcBtnUpMask;
    LONG    lcInOrgX;
    LONG    lcInOrgY;
    LONG    lcInOrgZ;
    LONG    lcInExtX;
    LONG    lcInExtY;
    LONG    lcInExtZ;
    LONG    lcOutOrgX;
    LONG    lcOutOrgY;
    LONG    lcOutOrgZ;
    LONG    lcOutExtX;
    LONG    lcOutExtY;
    LONG    lcOutExtZ;
    FIX32   lcSensX;
    FIX32   lcSensY;
    FIX32   lcSensZ;
    BOOL    lcSysMode;
    LONG    lcSysOrgX;
    LONG    lcSysOrgY;
    LONG    lcSysExtX;
    LONG    lcSysExtY;
    FIX32   lcSysSensX;
    FIX32   lcSysSensY;
} LOGCONTEXTA;

// ── ORIENTATION ─────────────────────────────────────────────────

typedef struct tagORIENTATION {
    int orAzimuth;
    int orAltitude;
    int orTwist;
} ORIENTATION;

// ── AXIS ────────────────────────────────────────────────────────

typedef struct tagAXIS {
    LONG    axMin;
    LONG    axMax;
    UINT    axUnits;
    FIX32   axResolution;
} AXIS;

// ── PACKET ──────────────────────────────────────────────────────
// Full packet with all standard data items.

typedef struct tagPACKET {
    HCTX        pkContext;
    UINT        pkStatus;
    DWORD       pkTime;
    WTPKT       pkChanged;
    UINT        pkSerialNumber;
    UINT        pkCursor;
    DWORD       pkButtons;
    LONG        pkX;
    LONG        pkY;
    LONG        pkZ;
    UINT        pkNormalPressure;
    UINT        pkTangentPressure;
    ORIENTATION pkOrientation;
} PACKET;

#pragma pack(pop)

// ── Function pointer typedefs ───────────────────────────────────
// For dynamic loading of Wintab32.dll.

typedef UINT (WINAPI *WTINFOA_FUNC)   (UINT, UINT, LPVOID);
typedef HCTX (WINAPI *WTOPENA_FUNC)   (HWND, LOGCONTEXTA*, BOOL);
typedef BOOL (WINAPI *WTCLOSE_FUNC)   (HCTX);
typedef BOOL (WINAPI *WTENABLE_FUNC)  (HCTX, BOOL);
typedef BOOL (WINAPI *WTGETA_FUNC)    (HCTX, LOGCONTEXTA*);
typedef BOOL (WINAPI *WTPACKET_FUNC)  (HCTX, UINT, LPVOID);
typedef int  (WINAPI *WTPACKETSGET_FUNC)(HCTX, int, LPVOID);
typedef BOOL (WINAPI *WTOVERLAP_FUNC) (HCTX, BOOL);
