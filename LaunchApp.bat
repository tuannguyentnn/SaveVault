@echo off
title Game Save Backup Tool (.NET 10 + SQLite)
echo Dang khoi dong Game Save Backup Tool tren .NET 10...
cd /d "%~dp0"
dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj --no-build
if %ERRORLEVEL% neq 0 (
    echo.
    echo Phat hien loi hoac chua build, dang tien hanh build va chay lai...
    dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj
)
pause
