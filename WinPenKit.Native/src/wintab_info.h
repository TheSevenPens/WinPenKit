#pragma once
#include "pen_session.h"
#include "wintab/wintab.h"

namespace wintab {
// Internal seam for driver-free tests. The result uses pen_wintab_free_info ownership.
const PenWintabInfo* read_info(WTINFOW_FUNC query);
}
