@echo off
setlocal
set "probeArch=x64"
if /i "%~1"=="x86" set "probeArch=x86"
if /i not "%VSCMD_ARG_TGT_ARCH%"=="%probeArch%" (
  if not exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    echo Run this script in a Visual Studio Developer Command Prompt.
    exit /b 2
  )
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do call "%%i\VC\Auxiliary\Build\vcvarsall.bat" %probeArch% >nul
)
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0"
if /i "%probeArch%"=="x86" (
  if not exist x86 mkdir x86
  cl /nologo /W4 /WX /wd4191 /Tcinvestigate.c /Fo:x86\investigate.obj /Fe:x86\investigate.exe /link user32.lib
) else (
  cl /nologo /W4 /WX /wd4191 /Tcinvestigate.c /Fe:investigate.exe /link user32.lib
)
set result=%errorlevel%
popd
exit /b %result%
