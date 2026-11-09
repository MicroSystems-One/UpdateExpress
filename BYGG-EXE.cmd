@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "PROJECT=%ROOT%UpdateExpress"
set "OUT=%ROOT%Publisert"
set "TARGET=%OUT%\UpdateExpress_v0.7.2.6.41.exe"
cd /d "%PROJECT%" || exit /b 1
where dotnet >nul 2>&1
if errorlevel 1 (
 echo ERROR: .NET 10 SDK not found.
 pause
 exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"
if exist "%TARGET%" del /q "%TARGET%"
echo.
echo === Publish Update Express v0.7.2.6.41 ===
dotnet publish UpdateExpress.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%OUT%"
if errorlevel 1 goto :failed
if not exist "%OUT%\UpdateExpress.exe" goto :failed
move /Y "%OUT%\UpdateExpress.exe" "%TARGET%" >nul
if errorlevel 1 goto :failed
echo.
echo BUILD SUCCEEDED: "%TARGET%"
pause
exit /b 0
:failed
echo.
echo ERROR: Publish failed. No new installer EXE was produced.
pause
exit /b 1
