@echo off
setlocal
if not defined VSCMD_VER call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0"
cl /nologo /W4 /WX /wd4191 /Tcinvestigate.c /Fe:investigate.exe /link user32.lib
set result=%errorlevel%
popd
exit /b %result%
