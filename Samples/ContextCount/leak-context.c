/* leak-context.c -- open one Wintab context and die without closing it.
 *
 * The demonstration the other two exist to be compared against. A process that ends without
 * calling WTClose is what happens when an application is killed from a task manager, crashes, or
 * is stopped from a debugger -- and on the driver this was tested against, the context it held is
 * retained after exit. A manager can reclaim known leaked contexts; see the investigation.
 *
 * TerminateProcess rather than exit(), because exit() runs the C runtime's shutdown and the point
 * is to model a process that gets no shutdown at all.
 *
 * Build:  cl /W4 leak-context.c user32.lib
 */

#include "wtcount.h"

int main(void)
{
    if (!wtLoad()) return 1;

    HWND window = wtWindow("leak-context");

    report("before opening");

    LOGCONTEXTA lc;
    if (!wtInfo(WTI_DEFSYSCTX, 0, &lc)) return 1;

    lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
    wantEverything(&lc);

    HANDLE context = wtOpen(window, &lc, TRUE);
    if (!context) {
        printf("  WTOpenA returned NULL -- the driver refused\n");
        return 1;
    }

    report("with one open");
    printf("  about to be killed while holding it\n");
    fflush(stdout);

    TerminateProcess(GetCurrentProcess(), 0);
    return 0;   /* not reached */
}
