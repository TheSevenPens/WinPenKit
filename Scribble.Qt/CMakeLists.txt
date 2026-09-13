cmake_minimum_required(VERSION 3.21)
project(ScribbleQt LANGUAGES CXX)

# The seventh Scribble sample, and the only one that does not use WinPenKit. It exists to be
# compared against the other six: a second, independent implementation of the same pen stack,
# so a measurement taken here says something about Windows rather than about this repository.
#
# Qt is found rather than vendored. Point CMAKE_PREFIX_PATH at a Qt 6 installation, e.g.
#   cmake -B build -DCMAKE_PREFIX_PATH=C:/Qt/6.8.3/msvc2022_64

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

# GuiPrivate for QNativeInterface::Private::QWindowsApplication, the only route to Qt's
# tablet backend. See src/qtbackend.h for why that is unavoidable.
find_package(Qt6 REQUIRED COMPONENTS Widgets Gui)

qt_standard_project_setup()

qt_add_executable(ScribbleQt WIN32
    src/main.cpp
    src/penapi.h
    src/qtbackend.cpp
    src/qtbackend.h
    src/ribbon.cpp
    src/ribbon.h
    src/scribblewindow.cpp
    src/scribblewindow.h
)

target_link_libraries(ScribbleQt PRIVATE Qt6::Widgets Qt6::GuiPrivate)

# The self test is shared with Scribble.Win32 rather than copied. The report ids, the line
# format and the recording format have to match exactly or the comparison this sample exists
# for is not a comparison. That header depends on nothing but <windows.h> and the standard
# library, which is what makes sharing it possible across a boundary this sample otherwise
# keeps: no WinPenKit headers, no WinPenKit.Native.lib.
target_include_directories(ScribbleQt PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../Scribble.Win32/src)

# NOMINMAX because <windows.h>'s min/max macros break Qt headers, WIN32_LEAN_AND_MEAN to keep
# the rest of the Win32 surface out of a translation unit that only wants the window calls.
target_compile_definitions(ScribbleQt PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)

if (MSVC)
    target_compile_options(ScribbleQt PRIVATE /W4 /permissive-)
endif()
