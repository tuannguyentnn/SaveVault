@echo off
chcp 65001 >nul
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_exe.ps1"

if errorlevel 1 (
    echo.
    echo ===================================================================
    echo [THAT BAI] Qua trinh dong goi gap su co hoac bi huy bo.
    echo ===================================================================
    echo.
)

pause
