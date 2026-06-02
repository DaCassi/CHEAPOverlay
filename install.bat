@echo off
setlocal enabledelayedexpansion
title CHEAPOverlay - Setup

:: ── Self-elevate ──────────────────────────────────────────────────────────────
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator permission...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

title CHEAPOverlay - Setup (Administrator)
cls

echo.
echo  ============================================
echo   CHEAPOverlay - Setup
echo   Cassi's Hyperspecific Extremely Arbitrary
echo   Portable Overlay
echo  ============================================
echo.
echo  This will install:
echo.
echo   - .NET 8 Runtime (if not installed)
echo   - CHEAPOverlay service
echo   - Windows startup shortcut
echo.
pause

set BASE=%~dp0
set SVCDIR=%BASE%service
set SRCDIR=%BASE%src
set PLGDIR=%BASE%plugins\YargBridge
set STATEFILE=%BASE%install_state.cfg
set DOTNET_WE_INSTALLED=0
set BEPINEX_WE_INSTALLED=0
set TMPDIR=%~dp0tmp

if not exist "%SVCDIR%" mkdir "%SVCDIR%"
if not exist "%SRCDIR%" mkdir "%SRCDIR%"

:: ── Copy source files ──────────────────────────────────────────────────────────
echo.
echo  [1/4] Preparing source files...
copy /y "%BASE%Program.cs"       "%SRCDIR%\Program.cs"       >nul
copy /y "%BASE%CHEAPOverlay.csproj" "%SRCDIR%\CHEAPOverlay.csproj" >nul
echo  [OK] Source files ready.

:: ── Check for .NET SDK ─────────────────────────────────────────────────────────
echo.
echo  [2/4] Checking for .NET SDK...
dotnet --version >nul 2>&1
if %errorLevel% equ 0 (
    for /f "tokens=*" %%v in ('dotnet --version') do echo  [OK] .NET SDK %%v found.
    goto compile
)

echo  .NET SDK not found. Downloading .NET 8...
if not exist "%TMPDIR%" mkdir "%TMPDIR%"
powershell -Command "& { $ProgressPreference='SilentlyContinue'; Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%TMPDIR%\dotnet-install.ps1' -UseBasicParsing }"
if not exist "%TMPDIR%\dotnet-install.ps1" (
    echo  [!!] Failed to download .NET installer.
    pause & exit /b 1
)
powershell -ExecutionPolicy Bypass -File "%TMPDIR%\dotnet-install.ps1" -Channel 8.0 -InstallDir "%ProgramFiles%\dotnet"
set "PATH=%PATH%;%ProgramFiles%\dotnet"
set DOTNET_WE_INSTALLED=1
echo  [OK] .NET 8 installed.

:compile
:: ── Compile service ────────────────────────────────────────────────────────────
echo.
echo  [3/4] Compiling overlay service...
cd /d "%SRCDIR%"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%SVCDIR%" -tl:off 2>&1 | findstr /i "error\|warning\|published"
if not exist "%SVCDIR%\cheapo-service.exe" (
    echo  [!!] Compilation failed. Make sure .NET SDK is installed.
    echo       Download from: https://dotnet.microsoft.com/download
    pause & exit /b 1
)
echo  [OK] Service compiled.

:: ── Startup shortcut ──────────────────────────────────────────────────────────
echo.
echo  [4/4] Installing startup shortcut...
set STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
set STARTBAT=%SVCDIR%\start-service.bat
set SVCEXE=cheapo-service.exe
(
    echo @echo off
    echo start "" /B "%SVCDIR%\!SVCEXE!"
) > "%STARTBAT%"
copy /y "%STARTBAT%" "%STARTUP%\cheapoverlay-service.bat" >nul
echo  [OK] Will start automatically with Windows.

:: ── Write state file ──────────────────────────────────────────────────────────
(
    echo DOTNET_INSTALLED=!DOTNET_WE_INSTALLED!
    echo STARTUP_INSTALLED=1
    echo YARG_PLUGIN_DIR=
    echo BEPINEX_INSTALLED_DIR=
) > "%STATEFILE%"

:: ── Optional: YARG plugin ─────────────────────────────────────────────────────
echo.
echo  ---------------------------------------------
echo   Optional: YARG Star Power Plugin
echo  ---------------------------------------------
echo.
echo  CHEAPOverlay can hook into YARG's star power
echo  via a BepInEx plugin. BepInEx will be downloaded
echo  and installed automatically if not already present.
echo.
set /p INSTALL_YARG=  Install YARG plugin? [Y/N]:
if /i "!INSTALL_YARG!" neq "Y" goto skip_yarg

:: ── Find YARG ─────────────────────────────────────────────────────────────────
echo.
echo  Searching for YARG installs...
echo  (Checking common locations first, then full scan if needed.)
echo.

if not exist "%TMPDIR%" mkdir "%TMPDIR%"
set SCANSCRIPT=%TMPDIR%\find_yarg.ps1

(
echo $found = @^(^)
echo $quick = @^(
echo     "$env:LOCALAPPDATA\YARC\YARG Installs",
echo     "$env:LOCALAPPDATA\Programs",
echo     "$env:PROGRAMFILES",
echo     "${env:PROGRAMFILES^(X86^)}",
echo     "$env:USERPROFILE\Downloads",
echo     "$env:USERPROFILE\Desktop",
echo     "$env:USERPROFILE\Documents",
echo     "$env:USERPROFILE\Games",
echo     "C:\Games","D:\Games","E:\Games","F:\Games","G:\Games"
echo ^)
echo try {
echo     $sp = ^(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -EA Stop^).SteamPath
echo     $quick += "$sp\steamapps\common"
echo } catch {}
echo foreach ^($p in $quick^) {
echo     if ^(Test-Path $p^) {
echo         Get-ChildItem $p -Filter YARG.exe -Recurse -Depth 6 -EA SilentlyContinue -Force ^|
echo             Where-Object { Test-Path ^(Join-Path $_.DirectoryName 'YARG_Data'^) } ^|
echo             ForEach-Object { $found += $_ }
echo     }
echo }
echo if ^($found.Count -eq 0^) {
echo     Write-Host "  Quick scan found nothing. Scanning all drives (this may take a minute)..." -ForegroundColor Yellow
echo     Get-PSDrive -PSProvider FileSystem ^| Where-Object { $_.Used -ne $null } ^| ForEach-Object {
echo         Get-ChildItem $_.Root -Filter YARG.exe -Recurse -EA SilentlyContinue -Force ^|
echo             Where-Object { Test-Path ^(Join-Path $_.DirectoryName 'YARG_Data'^) } ^|
echo             ForEach-Object { $found += $_ }
echo     }
echo }
echo if ^($found.Count -eq 0^) { Write-Host 'NOT_FOUND'; exit 1 }
echo $best = $found ^| Sort-Object {
echo     ^(Get-Item $_.DirectoryName -EA SilentlyContinue^).LastWriteTime
echo } -Descending ^| Select-Object -First 1
echo $best.DirectoryName
) > "%SCANSCRIPT%"

for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCANSCRIPT%"`) do (
    set SCAN_LINE=%%P
    if "!SCAN_LINE!" neq "NOT_FOUND" set YARG_DIR=!SCAN_LINE!
)

del "%SCANSCRIPT%" >nul 2>&1

if not defined YARG_DIR (
    echo.
    echo  [!!] No YARG install found on any drive.
    echo       Make sure YARG is installed and try again.
    echo       Skipping YARG plugin.
    goto skip_yarg
)
if "!YARG_DIR!" == "NOT_FOUND" (
    echo  [!!] YARG not found. Skipping plugin.
    goto skip_yarg
)

echo  Found: !YARG_DIR!

:: ── Install BepInEx if absent ────────────────────────────────────────────────
if exist "!YARG_DIR!\BepInEx\plugins" (
    echo  BepInEx already present - skipping download.
) else (
    echo.
    echo  BepInEx not found. Downloading latest BepInEx 5.x...
    set BEPINEX_SCRIPT=!TMPDIR!\get_bepinex.ps1
    set BEPINEX_ZIP=!TMPDIR!\bepinex.zip
    set YARG_DIR_COPY=!YARG_DIR!
    (
        echo $ErrorActionPreference = 'Stop'
        echo $ProgressPreference    = 'SilentlyContinue'
        echo $releases = Invoke-RestMethod 'https://api.github.com/repos/BepInEx/BepInEx/releases' -UseBasicParsing
        echo $rel = $releases ^| Where-Object { $_.tag_name -match '^v5\.' -and -not $_.prerelease } ^| Select-Object -First 1
        echo if ^(-not $rel^) { throw 'No BepInEx 5.x stable release found' }
        echo $asset = $rel.assets ^| Where-Object { $_.name -match '^BepInEx_win_x64_' } ^| Select-Object -First 1
        echo if ^(-not $asset^) { throw 'No win_x64 asset in that release' }
        echo Invoke-WebRequest $asset.browser_download_url -OutFile '!BEPINEX_ZIP!' -UseBasicParsing
        echo Expand-Archive '!BEPINEX_ZIP!' -DestinationPath '!YARG_DIR_COPY!' -Force
        echo $plg = Join-Path '!YARG_DIR_COPY!' 'BepInEx\plugins'
        echo if ^(Test-Path $plg -PathType Leaf^) { Remove-Item $plg -Force }
        echo if ^(-not ^(Test-Path $plg^)^) { New-Item -ItemType Directory -Path $plg -Force ^| Out-Null }
        echo Write-Host ^('[OK] BepInEx ' + $rel.tag_name + ' installed'^)
    ) > "!BEPINEX_SCRIPT!"
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BEPINEX_SCRIPT!"
    del "!BEPINEX_SCRIPT!" >nul 2>&1
    del "!BEPINEX_ZIP!"    >nul 2>&1
    if not exist "!YARG_DIR!\BepInEx\plugins" (
        echo  [!!] BepInEx installation failed.
        echo       Skipping YARG plugin.
        goto skip_yarg
    )
    set BEPINEX_WE_INSTALLED=1
    echo  [OK] BepInEx installed.
)

:: ── Build plugin ──────────────────────────────────────────────────────────────
echo.
echo  Building YARG bridge plugin...
cd /d "%PLGDIR%"
dotnet build -c Release -p:YargDir="!YARG_DIR!" -tl:off 2>&1 | findstr /i "error\|warning\|build succeeded\|build failed"

set PLUGIN_DLL=%PLGDIR%\bin\Release\net46\CHEAPOverlay.YargBridge.dll
if not exist "!PLUGIN_DLL!" (
    echo  [!!] Plugin build failed. Check that YargDir is correct and YARG DLLs exist.
    echo       Skipping YARG plugin.
    goto skip_yarg
)

:: ── Copy DLL ──────────────────────────────────────────────────────────────────
copy /y "!PLUGIN_DLL!" "!YARG_DIR!\BepInEx\plugins\CHEAPOverlay.YargBridge.dll" >nul
echo  [OK] Plugin installed to !YARG_DIR!\BepInEx\plugins\

:: ── Update state file with YARG plugin and BepInEx paths ─────────────────────
set BEPINEX_INSTALLED_DIR_VALUE=
if "!BEPINEX_WE_INSTALLED!"=="1" set BEPINEX_INSTALLED_DIR_VALUE=!YARG_DIR!
(
    echo DOTNET_INSTALLED=!DOTNET_WE_INSTALLED!
    echo STARTUP_INSTALLED=1
    echo YARG_PLUGIN_DIR=!YARG_DIR!\BepInEx\plugins
    echo BEPINEX_INSTALLED_DIR=!BEPINEX_INSTALLED_DIR_VALUE!
) > "%STATEFILE%"

:skip_yarg

:: ── Cleanup tmp ───────────────────────────────────────────────────────────────
if exist "%TMPDIR%" rmdir /s /q "%TMPDIR%" >nul 2>&1

:: ── Launch service ────────────────────────────────────────────────────────────
echo.
echo  ============================================
echo   Setup complete!
echo  ============================================
echo.
echo  Plug in your instruments, then press any key.
echo  The overlay service will start.
echo.
pause

cd /d "%BASE%"
set SVCEXE=cheapo-service.exe
start "" /B "%SVCDIR%\%SVCEXE%"

echo.
echo  Done! Add cheapoverlay.html to OBS as a
echo  Browser Source with Page Transparency on.
echo.
echo  The service starts automatically on boot.
echo  Run uninstall.bat to remove everything.
echo.
pause
