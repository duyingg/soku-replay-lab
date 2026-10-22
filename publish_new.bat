@echo off
setlocal
cd /d "%~dp0"
call build_native.bat
if errorlevel 1 goto failed
dotnet build SokuReplayLab.csproj -c Release ^
  -p:BaseOutputPath=bin/QA/ -p:BaseIntermediateOutputPath=obj/QA/
if errorlevel 1 goto failed
dotnet publish SokuReplayLab.csproj -c Release --no-restore ^
  -p:BaseOutputPath=bin/QA/ -p:BaseIntermediateOutputPath=obj/QA/ ^
  -o publish/SokuReplayLab
if errorlevel 1 goto failed
echo Published app: %CD%\publish\SokuReplayLab\SokuReplayLab.exe
echo Requires the .NET 9 Desktop Runtime on the destination PC.
exit /b 0
:failed
echo Portable build failed.
exit /b 1
