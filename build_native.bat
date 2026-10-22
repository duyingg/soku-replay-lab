@echo off
setlocal
set "RL_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%RL_VSWHERE%" exit /b 1
for /f "usebackq tokens=*" %%i in (`"%RL_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "RL_VS=%%i"
if not defined RL_VS exit /b 1
call "%RL_VS%\VC\Auxiliary\Build\vcvars32.bat" >nul
if errorlevel 1 exit /b 1
cd /d "%~dp0"
set "RL_PROBE_OUT=NativeProbe.dll"
if /I "%~1"=="next" set "RL_PROBE_OUT=NativeProbe.next.dll"
cl /nologo /LD /EHsc /std:c++17 /O2 /W4 NativeProbe.cpp /link /OUT:%RL_PROBE_OUT% user32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /EHsc /std:c++17 /O2 /W4 NativeAttach.cpp /link /OUT:NativeAttach.exe advapi32.lib user32.lib
exit /b %errorlevel%
