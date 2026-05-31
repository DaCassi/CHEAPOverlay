@echo off
setlocal enabledelayedexpansion
title CHEAPOverlay - Uninstall

:: ── Self-elevate ──────────────────────────────────────────────────────────────
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator permission...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

title CHEAPOverlay - Uninstall (Administrator)
cls

echo.
echo  ============================================
echo   CHEAPOverlay - Uninstall
echo  ============================================
echo.
echo  This will remove everything CHEAPOverlay
echo  installed outside its own folder:
echo.
echo   - Windows startup shortcut
echo   - YARG BepInEx plugin (if installed)
echo   - BepInEx itself (if we installed it)
echo   - .NET 8 (only if this installer put it there)
echo.
echo  The CHEAPOverlay folder itself is NOT deleted.
echo  You can delete it manually afterward.
echo.
pause

set BASE=%~dp0
set STATEFILE=%BASE%install_state.cfg
set SVCDIR=%BASE%service
set STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup

:: ── Read state file ───────────────────────────────────────────────────────────
set DOTNET_INSTALLED=0
set STARTUP_INSTALLED=0
set YARG_PLUGIN_DIR=
set BEPINEX_INSTALLED_DIR=

if exist "%STATEFILE%" (
    for /f "tokens=1,* delims==" %%A in (%STATEFILE%) do (
        if "%%A"=="DOTNET_INSTALLED"      set DOTNET_INSTALLED=%%B
        if "%%A"=="STARTUP_INSTALLED"     set STARTUP_INSTALLED=%%B
        if "%%A"=="YARG_PLUGIN_DIR"       set YARG_PLUGIN_DIR=%%B
        if "%%A"=="BEPINEX_INSTALLED_DIR" set BEPINEX_INSTALLED_DIR=%%B
    )
)

:: ── [1/5] Kill service ────────────────────────────────────────────────────────
echo.
echo  [1/5] Stopping CHEAPOverlay service...
taskkill /f /im cheapo-service.exe >nul 2>&1
echo  [OK] Service stopped (or was not running).

:: ── [2/5] Remove startup shortcut ────────────────────────────────────────────
echo.
echo  [2/5] Removing startup shortcut...
set REMOVED_STARTUP=0
if exist "%STARTUP%\cheapoverlay-service.bat" (
    del /f /q "%STARTUP%\cheapoverlay-service.bat" >nul 2>&1
    set REMOVED_STARTUP=1
)
if exist "%STARTUP%\gh-overlay-service.bat" (
    del /f /q "%STARTUP%\gh-overlay-service.bat" >nul 2>&1
    set REMOVED_STARTUP=1
)
if "!REMOVED_STARTUP!"=="1" (
    echo  [OK] Startup shortcut removed.
) else (
    echo  [--] No startup shortcut found.
)

:: ── [3/5] Remove YARG plugin DLL ─────────────────────────────────────────────
echo.
echo  [3/5] Removing YARG plugin...
set PLUGIN_REMOVED=0
if defined YARG_PLUGIN_DIR (
    if "!YARG_PLUGIN_DIR!" neq "" (
        set PLUGIN_FILE=!YARG_PLUGIN_DIR!\CHEAPOverlay.YargBridge.dll
        if exist "!PLUGIN_FILE!" (
            del /f /q "!PLUGIN_FILE!" >nul 2>&1
            echo  [OK] Removed !PLUGIN_FILE!
            set PLUGIN_REMOVED=1
        ) else (
            echo  [--] Plugin not at recorded path: !YARG_PLUGIN_DIR!
        )
    )
)
:: Also sweep all drives for any stray copies
echo  Sweeping drives for any other copies...
powershell -NoProfile -Command ^
    "Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -ne $null } | ForEach-Object { Get-ChildItem $_.Root -Filter 'CHEAPOverlay.YargBridge.dll' -Recurse -EA SilentlyContinue -Force } | ForEach-Object { Remove-Item $_.FullName -Force; Write-Host \"  [OK] Removed $($_.FullName)\" }"
if "!PLUGIN_REMOVED!"=="0" echo  [--] No YARG plugin DLL found.

:: ── [4/5] Remove BepInEx (only if we installed it) ───────────────────────────
echo.
echo  [4/5] BepInEx...
if "!BEPINEX_INSTALLED_DIR!"=="" (
    echo  [--] BepInEx was pre-existing or not installed - nothing to remove.
    goto skip_bepinex
)
if not exist "!BEPINEX_INSTALLED_DIR!" (
    echo  [--] Recorded YARG directory no longer exists: !BEPINEX_INSTALLED_DIR!
    goto skip_bepinex
)

echo  Removing BepInEx from !BEPINEX_INSTALLED_DIR!...
if exist "!BEPINEX_INSTALLED_DIR!\BepInEx" (
    rmdir /s /q "!BEPINEX_INSTALLED_DIR!\BepInEx" >nul 2>&1
    echo  [OK] Removed BepInEx\ folder.
)
if exist "!BEPINEX_INSTALLED_DIR!\winhttp.dll"        del /f /q "!BEPINEX_INSTALLED_DIR!\winhttp.dll"        >nul 2>&1 & echo  [OK] Removed winhttp.dll
if exist "!BEPINEX_INSTALLED_DIR!\doorstop_config.ini" del /f /q "!BEPINEX_INSTALLED_DIR!\doorstop_config.ini" >nul 2>&1 & echo  [OK] Removed doorstop_config.ini
if exist "!BEPINEX_INSTALLED_DIR!\.doorstop_version"   del /f /q "!BEPINEX_INSTALLED_DIR!\.doorstop_version"   >nul 2>&1 & echo  [OK] Removed .doorstop_version

:skip_bepinex

:: ── [5/5] Remove .NET (only if we installed it) ──────────────────────────────
echo.
echo  [5/5] .NET check...
if "!DOTNET_INSTALLED!" neq "1" (
    echo  [--] .NET was pre-existing - nothing to remove.
    goto skip_dotnet
)

echo.
echo  CHEAPOverlay installed .NET 8 to:
echo    %ProgramFiles%\dotnet
echo.
echo  WARNING: Other applications may use .NET.
echo  Removing it could break other software.
echo.
set /p REMOVE_DOTNET=  Remove .NET 8 anyway? [Y/N]:
if /i "!REMOVE_DOTNET!" neq "Y" (
    echo  [--] Skipped .NET removal.
    goto skip_dotnet
)
if exist "%ProgramFiles%\dotnet" (
    rmdir /s /q "%ProgramFiles%\dotnet" >nul 2>&1
    echo  [OK] Removed %ProgramFiles%\dotnet
) else (
    echo  [--] %ProgramFiles%\dotnet not found.
)

:skip_dotnet

:: ── Remove state file ─────────────────────────────────────────────────────────
if exist "%STATEFILE%" del /f /q "%STATEFILE%" >nul 2>&1

echo.
echo  ============================================
echo   Uninstall complete.
echo  ============================================
echo.
echo  Everything outside this folder has been removed.
echo  You can now delete the CHEAPOverlay folder:
echo    %BASE%
echo.
pause
