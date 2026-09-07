@echo off
cd /d "%~dp0"
echo =========================================================
echo       SaveVault - Build Executable (.EXE)
echo =========================================================
echo.
echo Dang tien hanh build SaveVault sang file .exe...
echo Vui long cho trong giay lat...
echo.

if not exist "publish" mkdir "publish"

dotnet publish SaveGameBackup.UI\SaveGameBackup.UI.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "publish"

if errorlevel 1 goto BUILD_ERROR

echo.
echo =========================================================
echo [THANH CONG] File SaveVault.exe da duoc tao thanh cong!
echo Duong dan: "%~dp0publish\SaveVault.exe"
echo =========================================================
echo.
start "" "%~dp0publish"
goto END

:BUILD_ERROR
echo.
echo [LOI] Qua trinh build that bai. Vui long kiem tra thong tin loi ben tren.
echo.

:END
pause
