# Release

## Version
This app is currently at version 3.0.0

## Wacom Tablet Driver Compatibility
WintabDN (ExtensionTestApp, LegacyPenTestApp, and WintabDN), which uses the Wintab API, are compatible with the latest Wacom tablet drivers.

## History
* Release 3.0.0 26 March 2026
	* Migrated to .NET 10 and C# 14
	* Converted to SDK-style project format (SLNX)
	* Replaced deprecated ReaderWriterLock with ReaderWriterLockSlim
	* Removed obsolete Code Access Security (CAS) attributes
	* Removed all MessageBox.Show from library; exceptions now propagate
	* Added UnmanagedBuffer IDisposable wrapper; fixed memory leaks
	* Added IDisposable to WintabContext and WintabData
	* Added custom exception hierarchy (WintabException, WintabContextException, etc.)
	* Added IWintabLog logging abstraction
	* Optimized MarshalDataExtPackets (eliminated per-packet allocations)
	* Modernized LegacyPenTestApp (renamed LegacyPenTestApp): nullable refs, string interpolation, resource disposal, DPI-aware scribble drawing
	* Added live pen data status bar (position, pressure, tilt, buttons)
	* Fixed scribble drawing for .NET 10 (bitmap-backed rendering, double-buffered)
	* Throttled scribble rendering to ~60fps via render timer (batched repaints)
	* DPI-aware coordinate handling using raw Wintab coords with PointToClient
	* Added WintabDpiHelper utility for non-WinForms frameworks
	* Added System/Digitizer context switcher for scribble mode
	* Removed System.Windows.Forms and System.Drawing dependencies from library (framework-agnostic)
	* Replaced Form-based message window with raw Win32 P/Invoke
	* Replaced System.Windows.Forms.Message with WintabMessage struct
	* Decomposed TestForm into ScribbleRenderer, TabletDataCapture, WintabTestRunner
	* Enabled nullable reference types and implicit usings across WintabDN library
	* Modern C#: primary constructors, pattern matching switch expressions
	* API naming cleanup: removed Hungarian E prefix from enums, shortened method names (breaking change)
	* File-scoped namespaces across all source files
	* Added Scribble.WinUI project (WinUI 3 scribble demo using framework-agnostic WintabDN)
* Release 2.1.0 5 January 2022
	* Updated to support targeting x64 CPUs
* Release 2.0.0 10 July 2020  
	* Updated for release to Github
* Releases prior to 2.0.0 (various dates)
	* Maintenance releases not versioned
