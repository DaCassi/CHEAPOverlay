@echo off
setlocal enabledelayedexpansion
title CHEAPOverlay - Repair YARG Plugin

cls
echo.
echo  ============================================
echo   CHEAPOverlay - Repair YARG Plugin
echo  ============================================
echo.
echo  Reinstalls BepInEx and the star power plugin
echo  into your YARG folder. Run this after every
echo  YARG update.
echo.

set BASE=%~dp0
set STATEFILE=%BASE%install_state.cfg
set PLGDIR=%BASE%plugins\YargBridge
set TMPDIR=%BASE%tmp
set YARG_DIR=

if not exist "%TMPDIR%" mkdir "%TMPDIR%"

:: ── Find YARG dir from state file ─────────────────────────────────────────────
if exist "%STATEFILE%" (
    for /f "tokens=1,* delims==" %%A in (%STATEFILE%) do (
        if "%%A"=="BEPINEX_INSTALLED_DIR" if "%%B" neq "" set YARG_DIR=%%B
    )
    :: Fall back to deriving from YARG_PLUGIN_DIR if BEPINEX_INSTALLED_DIR was empty
    if "!YARG_DIR!"=="" (
        for /f "tokens=1,* delims==" %%A in (%STATEFILE%) do (
            if "%%A"=="YARG_PLUGIN_DIR" if "%%B" neq "" (
                set _P=%%B
                set YARG_DIR=!_P:\BepInEx\plugins=!
            )
        )
    )
)

:: ── Scan if still not found ───────────────────────────────────────────────────
if "!YARG_DIR!"=="" (
    echo  No recorded YARG path. Scanning...
    set SCANSCRIPT=%TMPDIR%\find_yarg.ps1
    (
        echo $ProgressPreference = 'SilentlyContinue'
        echo $found = @^(^)
        echo $quick = @^(
        echo     "$env:LOCALAPPDATA\YARC\YARG Installs",
        echo     "$env:LOCALAPPDATA\Programs",
        echo     "$env:PROGRAMFILES",
        echo     "${env:PROGRAMFILES^(X86^)}",
        echo     "$env:USERPROFILE\Downloads",
        echo     "$env:USERPROFILE\Desktop",
        echo     "$env:USERPROFILE\Games",
        echo     "C:\Games","D:\Games","E:\Games","F:\Games","G:\Games"
        echo ^)
        echo foreach ^($p in $quick^) {
        echo     if ^(Test-Path $p^) {
        echo         Get-ChildItem $p -Filter YARG.exe -Recurse -Depth 6 -EA SilentlyContinue -Force ^|
        echo             Where-Object { Test-Path ^(Join-Path $_.DirectoryName 'YARG_Data'^) } ^|
        echo             ForEach-Object { $found += $_ }
        echo     }
        echo }
        echo if ^($found.Count -eq 0^) {
        echo     Get-PSDrive -PSProvider FileSystem ^| Where-Object { $_.Used -ne $null } ^| ForEach-Object {
        echo         Get-ChildItem $_.Root -Filter YARG.exe -Recurse -EA SilentlyContinue -Force ^|
        echo             Where-Object { Test-Path ^(Join-Path $_.DirectoryName 'YARG_Data'^) } ^|
        echo             ForEach-Object { $found += $_ }
        echo     }
        echo }
        echo if ^($found.Count -eq 0^) { exit 1 }
        echo ^($found ^| Sort-Object { ^(Get-Item $_.DirectoryName -EA SilentlyContinue^).LastWriteTime } -Desc ^| Select-Object -First 1^).DirectoryName
    ) > "%SCANSCRIPT%"
    for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCANSCRIPT%"`) do set YARG_DIR=%%P
    del "%SCANSCRIPT%" >nul 2>&1
)

if "!YARG_DIR!"=="" (
    echo  [!!] Could not find YARG. Make sure YARG is installed.
    pause & exit /b 1
)

echo  YARG: !YARG_DIR!

:: ── Install BepInEx if doorstop is missing ────────────────────────────────────
echo.
if not exist "!YARG_DIR!\winhttp.dll" (
    echo  [1/3] BepInEx not found. Downloading...
    set BEPINEX_SCRIPT=%TMPDIR%\get_bepinex.ps1
    set BEPINEX_ZIP=%TMPDIR%\bepinex.zip
    set YARG_DIR_COPY=!YARG_DIR!
    (
        echo $ProgressPreference = 'SilentlyContinue'
        echo $ErrorActionPreference = 'Stop'
        echo $releases = Invoke-RestMethod 'https://api.github.com/repos/BepInEx/BepInEx/releases' -UseBasicParsing
        echo $rel = $releases ^| Where-Object { $_.tag_name -match '^v5\.' -and -not $_.prerelease } ^| Select-Object -First 1
        echo $asset = $rel.assets ^| Where-Object { $_.name -match '^BepInEx_win_x64_' } ^| Select-Object -First 1
        echo Invoke-WebRequest $asset.browser_download_url -OutFile '%BEPINEX_ZIP%' -UseBasicParsing
        echo Expand-Archive '%BEPINEX_ZIP%' -DestinationPath '%YARG_DIR_COPY%' -Force
        echo $plg = Join-Path '%YARG_DIR_COPY%' 'BepInEx\plugins'
        echo if ^(Test-Path $plg -PathType Leaf^) { Remove-Item $plg -Force }
        echo if ^(-not ^(Test-Path $plg^)^) { New-Item -ItemType Directory -Path $plg -Force ^| Out-Null }
        echo Write-Host ^('[OK] BepInEx ' + $rel.tag_name^)
    ) > "%BEPINEX_SCRIPT%"
    powershell -NoProfile -ExecutionPolicy Bypass -File "%BEPINEX_SCRIPT%"
    del "%BEPINEX_SCRIPT%" >nul 2>&1
    del "%BEPINEX_ZIP%"    >nul 2>&1
    if not exist "!YARG_DIR!\winhttp.dll" (
        echo  [!!] BepInEx install failed.
        pause & exit /b 1
    )
) else (
    echo  [1/3] BepInEx present - skipping download.
)

:: Ensure plugins directory is a directory, not a file
powershell -NoProfile -Command "$p='!YARG_DIR!\BepInEx\plugins'; if(Test-Path $p -PathType Leaf){Remove-Item $p -Force}; if(-not(Test-Path $p)){New-Item -ItemType Directory $p -Force|Out-Null}" >nul 2>&1

:: ── Rebuild plugin ────────────────────────────────────────────────────────────
echo.
echo  [2/3] Building plugin against current YARG version...
cd /d "%PLGDIR%"
dotnet build -c Release -p:YargDir="!YARG_DIR!" -tl:off 2>&1 | findstr /i "error\|warning\|succeeded\|failed"
set PLUGIN_DLL=%PLGDIR%\bin\Release\net46\CHEAPOverlay.YargBridge.dll
if not exist "%PLUGIN_DLL%" (
    echo  [!!] Build failed.
    pause & exit /b 1
)

:: ── Copy DLL ──────────────────────────────────────────────────────────────────
echo.
echo  [3/3] Installing plugin...
copy /y "%PLUGIN_DLL%" "!YARG_DIR!\BepInEx\plugins\CHEAPOverlay.YargBridge.dll" >nul
echo  [OK] Done. Launch YARG normally to activate.

:: ── Update state file ─────────────────────────────────────────────────────────
if exist "%STATEFILE%" (
    powershell -NoProfile -Command "
        \$f = '%STATEFILE%'; \$lines = Get-Content \$f
        \$lines = \$lines | ForEach-Object {
            if (\$_ -match '^BEPINEX_INSTALLED_DIR=') { 'BEPINEX_INSTALLED_DIR=!YARG_DIR!' }
            elseif (\$_ -match '^YARG_PLUGIN_DIR=')    { 'YARG_PLUGIN_DIR=!YARG_DIR!\BepInEx\plugins' }
            else { \$_ }
        }
        \$lines | Set-Content \$f
    " >nul 2>&1
)

:: ── Cleanup ───────────────────────────────────────────────────────────────────
if exist "%TMPDIR%" rmdir /s /q "%TMPDIR%" >nul 2>&1

echo.
pause
