@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "PROJECT=%ROOT%UpdateExpress"
set "OUT=%ROOT%Publisert"
set "TARGET=%OUT%\UpdateExpress_v0.7.2.61.exe"
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
echo === Publish Update Express v0.7.2.61 ===
dotnet publish UpdateExpress.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%OUT%"
if errorlevel 1 goto :failed
if not exist "%OUT%\UpdateExpress.exe" goto :failed
move /Y "%OUT%\UpdateExpress.exe" "%TARGET%" >nul
if errorlevel 1 goto :failed
echo.
echo === Authenticode (dry-run until UE_SIGN_MODE is configured) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\Sign-Release.ps1" "%TARGET%"
if errorlevel 1 goto :signfailed
echo.
echo BUILD SUCCEEDED: "%TARGET%"
exit /b 0
:failed
echo.
echo ERROR: Publish failed. No new installer EXE was produced.
exit /b 1
:signfailed
echo.
echo ERROR: Publish succeeded but Authenticode signing failed.
echo        Fix Sign-Release / UE_SIGN_* env, or set UE_SIGN=0 to skip.
exit /b 1
