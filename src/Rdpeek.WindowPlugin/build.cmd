@echo off
setlocal EnableDelayedExpansion
rem Build rdpeek-window-plugin.dll (x64) with the MSVC toolchain (no .NET / NativeAOT needed).
rem Auto-locates Visual Studio's C++ tools via vswhere, then compiles with cl.exe.

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" (
    echo [window-plugin] vswhere.exe not found - install Visual Studio with the "Desktop development with C++" workload
    exit /b 1
)

set "VSINSTALL="
for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL (
    echo [window-plugin] no Visual Studio install with the C++ workload was found
    exit /b 1
)

call "!VSINSTALL!\Common7\Tools\VsDevCmd.bat" -arch=x64 -no_logo || exit /b 1

pushd "%~dp0"
if not exist bin mkdir bin
cl /nologo /LD /EHsc /O2 /W3 windowplugin.cpp ^
   /Fe:bin\rdpeek-window-plugin.dll /Fo:bin\ ^
   /link /DEF:rdpeek-window-plugin.def user32.lib gdi32.lib ole32.lib
set "RC=%ERRORLEVEL%"
popd
if "%RC%"=="0" ( echo [window-plugin] built bin\rdpeek-window-plugin.dll ) else ( echo [window-plugin] build failed with %RC% )
exit /b %RC%
