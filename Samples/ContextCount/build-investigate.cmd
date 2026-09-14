@echo off
setlocal
if not defined VSCMD_VER (
  if not exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    echo Run this script in a Visual Studio Developer Command Prompt.
    exit /b 2
  )
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do call "%%i\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
)
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0"
cl /nologo /W4 /WX /wd4191 /Tcinvestigate.c /Fe:investigate.exe /link user32.lib
set result=%errorlevel%
popd
exit /b %result%
