@echo off
setlocal EnableDelayedExpansion
rem Build rdpeek-vc-shim.dll (x64) with the MSVC toolchain (no .NET / NativeAOT needed).
rem Auto-locates Visual Studio's C++ tools via vswhere, then compiles with cl.exe.

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" (
    echo [vc-shim] vswhere.exe not found - install Visual Studio with the "Desktop development with C++" workload
    exit /b 1
)

set "VSINSTALL="
for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL (
    echo [vc-shim] no Visual Studio install with the C++ workload was found
    exit /b 1
)

call "!VSINSTALL!\Common7\Tools\VsDevCmd.bat" -arch=x64 -no_logo || exit /b 1

pushd "%~dp0"
if not exist bin mkdir bin
cl /nologo /LD /EHsc /O2 /W3 vcshim.cpp ^
   /Fe:bin\rdpeek-vc-shim.dll /Fo:bin\ ^
   /link /DEF:rdpeek-vc-shim.def ole32.lib
set "RC=%ERRORLEVEL%"
popd
if "%RC%"=="0" ( echo [vc-shim] built bin\rdpeek-vc-shim.dll ) else ( echo [vc-shim] build failed with %RC% )
exit /b %RC%
