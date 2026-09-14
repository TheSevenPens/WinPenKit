/* system-context.c -- open one Wintab SYSTEM context and count contexts either side of it.
 *
 * A system context is what an application opens when it wants the pen to drive the cursor as
 * well as deliver packets. It is the ordinary case, and it is what WinPenKit's Wintab sessions
 * ask for.
 *
 * Build:  cl /W4 system-context.c user32.lib
 */

#include "wtcount.h"

int main(void)
{
    if (!wtLoad()) return 1;

    HWND window = wtWindow("system-context");

    report("before opening");

    LOGCONTEXTA lc;
    if (!wtInfo(WTI_DEFSYSCTX, 0, &lc)) {
        printf("  the driver would not give its default system context\n");
        return 1;
    }

    /* CXO_SYSTEM is already set in the default system context; CXO_MESSAGES asks for the
       window messages that say a packet is waiting. */
    lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
    wantEverything(&lc);

    HANDLE context = wtOpen(window, &lc, TRUE);
    if (!context) {
        printf("  WTOpenA returned NULL -- the driver refused\n");
        report("after the refusal");
        return 1;
    }

    report("with one open");

    wtClose(context);
    report("after closing it");
    return 0;
}
