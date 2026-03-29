#pragma once

#include <cmath>
#include <cstdint>

namespace wintab {

// Implements the Wintab axis scaling equation with sign-flip handling.
// When in_ext and out_ext have the same sign, the mapping is direct.
// When they have opposite signs (Y-axis flip), the direction reverses.
// All arithmetic in double to preserve sub-pixel precision.
//
// Matches the C# WintabSession.ScaleAxis implementation exactly.

inline double scale_axis(int32_t input, int32_t in_org, int32_t in_ext,
                          int32_t out_org, int32_t out_ext) {
    if (in_ext == 0) return static_cast<double>(out_org);

    double d_in      = static_cast<double>(input);
    double d_in_org  = static_cast<double>(in_org);
    double d_in_ext  = static_cast<double>(in_ext);
    double d_out_org = static_cast<double>(out_org);
    double d_out_ext = static_cast<double>(out_ext);

    // Same sign: direct mapping. Opposite sign: reversed mapping.
    bool same_sign = (d_out_ext >= 0.0) == (d_in_ext >= 0.0);

    if (same_sign) {
        return ((d_in - d_in_org) * std::abs(d_out_ext) / std::abs(d_in_ext)) + d_out_org;
    } else {
        return ((std::abs(d_in_ext) - (d_in - d_in_org)) * std::abs(d_out_ext) / std::abs(d_in_ext)) + d_out_org;
    }
}

} // namespace wintab
