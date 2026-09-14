/* digitizer-context.c -- open one Wintab DIGITISING context and count contexts either side.
 *
 * A digitising context reports tablet-native coordinates and does not drive the system cursor.
 * The question this demo exists to answer is whether one is enough: an application that wants
 * tablet resolution *and* a working cursor might reasonably be thought to need two contexts, one
 * of each kind. It does not. One digitising context opens on its own.
 *
 * Build:  cl /W4 digitizer-context.c user32.lib
 */

#include "wtcount.h"

int main(void)
{
    if (!wtLoad()) return 1;

    HWND window = wtWindow("digitizer-context");

    report("before opening");

    LOGCONTEXTA lc;
    if (!wtInfo(WTI_DEFCONTEXT, 0, &lc)) {
        printf("  the driver would not give its default digitising context\n");
        return 1;
    }

    /* The distinguishing line: CXO_SYSTEM cleared, so this context does not move the cursor. */
    lc.lcOptions &= ~CXO_SYSTEM;
    lc.lcOptions |= CXO_MESSAGES;
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
