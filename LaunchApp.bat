@echo off
title SaveVault - Game Save Backup Tool (.NET 10 MAUI Blazor Hybrid)
echo Dang khoi dong SaveVault tren .NET 10 MAUI Blazor Hybrid...
cd /d "%~dp0"
dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj -f net10.0-windows10.0.19041.0 --no-build
if %ERRORLEVEL% neq 0 (
    echo.
    echo Phat hien chua build hoac can cap nhat, dang tien hanh build va chay...
    dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj -f net10.0-windows10.0.19041.0
)
pause
