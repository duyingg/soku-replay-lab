@echo off
setlocal
cd /d "%~dp0"
dotnet build SokuReplayLab.csproj --configuration Release -p:BaseOutputPath=bin/QA/ -p:BaseIntermediateOutputPath=obj/QA/
if errorlevel 1 goto failed
dotnet bin\QA\Release\net9.0-windows\SokuReplayLab.dll
:failed
if errorlevel 1 pause
