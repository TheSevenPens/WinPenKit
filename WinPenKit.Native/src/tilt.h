#pragma once

#include <algorithm>
#include <cmath>

namespace wintab {

// Converts between spherical tilt (azimuth, altitude) and planar tilt (tilt_x, tilt_y), all in
// degrees. The conversion is exact: tan(tilt_x) and tan(tilt_y) are the horizontal components
// of the pen's direction divided by its vertical component. The earlier linear form,
// tilt_x = (90 - altitude) * sin(azimuth), differs off the axes by up to 8.3 degrees at
// azimuth 45 and altitude 30.
//
// Sign convention: positive tilt_x is a tilt to the right, positive tilt_y a tilt toward the
// user, and azimuth = atan2(-tan tilt_x, tan tilt_y).
//
// Matches the C# PenTilt implementation exactly.

constexpr double kTiltPi = 3.14159265358979323846;
constexpr double kUprightThresholdDeg = 0.5; // below this from vertical, azimuth is 0

inline void spherical_to_planar(double azimuth, double altitude, double& tilt_x, double& tilt_y) {
    const double from_vertical = (90.0 - altitude) * kTiltPi / 180.0;
    const double az = azimuth * kTiltPi / 180.0;
    const double horizontal = std::sin(from_vertical);
    const double vertical = std::cos(from_vertical);
    tilt_x = -std::atan2(horizontal * std::sin(az), vertical) * 180.0 / kTiltPi;
    tilt_y =  std::atan2(horizontal * std::cos(az), vertical) * 180.0 / kTiltPi;
}

inline void planar_to_spherical(double tilt_x, double tilt_y, double& azimuth, double& altitude) {
    const double tx = std::tan((std::clamp)(tilt_x, -90.0, 90.0) * kTiltPi / 180.0);
    const double ty = std::tan((std::clamp)(tilt_y, -90.0, 90.0) * kTiltPi / 180.0);
    const double from_vertical = std::atan(std::sqrt(tx * tx + ty * ty)) * 180.0 / kTiltPi;
    altitude = (std::clamp)(90.0 - from_vertical, 0.0, 90.0);
    if (from_vertical <= kUprightThresholdDeg) {
        azimuth = 0.0;
        return;
    }
    const double deg = std::atan2(-tx, ty) * 180.0 / kTiltPi;
    azimuth = std::fmod(std::fmod(deg, 360.0) + 360.0, 360.0);
}

} // namespace wintab
