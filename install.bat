@echo off
rem ============================================================
rem  ChatSheet installer entry point. Double-click to run.
rem
rem  Keep this file ASCII-only. A .bat is read using the console's
rem  OEM codepage (936 on Chinese Windows), so Chinese text placed
rem  here renders as mojibake and can even break the parser.
rem  All user-facing text lives in scripts\menu.ps1, which is
rem  UTF-8 with BOM and sets its own console encoding.
rem
rem  Why a .bat at all: a .ps1 cannot be double-clicked - Windows
rem  opens it in an editor, and the default execution policy would
rem  block it anyway. This wrapper passes -ExecutionPolicy Bypass.
rem ============================================================

setlocal

rem UTF-8 codepage so the menu's Chinese text renders in this console.
chcp 65001 >nul 2>&1

set "MENU=%~dp0scripts\menu.ps1"

if not exist "%MENU%" (
    echo.
    echo   Cannot find: %MENU%
    echo.
    echo   Keep install.bat next to the scripts folder.
    echo   If you downloaded a release zip, extract it fully first.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%MENU%"
set "CODE=%errorlevel%"

rem Only pause on failure. On success the menu already waited for input,
rem so a second prompt would just be one more key to press.
if not "%CODE%"=="0" (
    echo.
    echo   Installer exited with code %CODE%.
    pause
)

exit /b %CODE%
