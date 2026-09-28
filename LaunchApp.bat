@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

:: ===================================================================
::   1. DOC PHIEN BAN TU version.json (Single Source of Truth)
:: ===================================================================
set "APP_VER=1.0.0"

:: Trich xuat nhanh bang findstr
if exist "version.json" (
    for /f "tokens=2 delims=:, " %%a in ('findstr /c:"\"version\":" version.json') do (
        set "APP_VER=%%~a"
    )
)

:: Fallback PowerShell neu findstr khong doc duoc
if "%APP_VER%"=="1.0.0" if exist "version.json" (
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content version.json -Raw | ConvertFrom-Json).version" 2^>nul`) do (
        if not "%%v"=="" set "APP_VER=%%v"
    )
)

title SaveVault v%APP_VER% - Game Save Backup Tool (.NET 10 MAUI Blazor Hybrid)

echo ===================================================================
echo   SaveVault v%APP_VER% [.NET 10 MAUI Blazor Hybrid]
echo ===================================================================
echo.

:: ===================================================================
::   2. KIEM TRA DONG BO PHIEN BAN & CACHE BUILD
:: ===================================================================
set "NEED_BUILD=0"
set "VERSION_CACHE_FILE=SaveGameBackup.UI\bin\.last_built_version"

if not exist "%VERSION_CACHE_FILE%" (
    set "NEED_BUILD=1"
) else (
    set /p LAST_VER=<"%VERSION_CACHE_FILE%"
    if "!LAST_VER!" neq "%APP_VER%" (
        set "NEED_BUILD=1"
    )
)

:: ===================================================================
::   3. KHOI CHAY UNG DUNG
:: ===================================================================
if "!NEED_BUILD!"=="1" (
    echo [DONG BO] Phat hien phien ban v%APP_VER% moi hoac chua bien dich.
    echo          Dang tien hanh build va khoi dong ung dung...
    echo.
    dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj -f net10.0-windows10.0.19041.0
    if !ERRORLEVEL! equ 0 (
        if not exist "SaveGameBackup.UI\bin" mkdir "SaveGameBackup.UI\bin"
        echo %APP_VER%> "%VERSION_CACHE_FILE%"
    )
) else (
    echo [KHOI DONG NHANH] Dang mo SaveVault v%APP_VER% [Fast Launch]...
    dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj -f net10.0-windows10.0.19041.0 --no-build
    if !ERRORLEVEL! neq 0 (
        echo.
        echo [CANH BAO] Ban build chua san sang hoac da bi xoa.
        echo          Dang tien hanh bien dich va chay lai...
        echo.
        dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj -f net10.0-windows10.0.19041.0
        if !ERRORLEVEL! equ 0 (
            if not exist "SaveGameBackup.UI\bin" mkdir "SaveGameBackup.UI\bin"
            echo %APP_VER%> "%VERSION_CACHE_FILE%"
        )
    )
)

echo.
pause
