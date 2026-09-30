param (
    [switch]$NonInteractive,
    [switch]$Force,
    [string]$PublishPath = ""
)

# ===================================================================
#               Omnisave - Build & Package Distribution
# ===================================================================
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# -------------------------------------------------------------------
# [BUOC 0] Doc phien ban tu version.json lam nguon goc duy nhat
# -------------------------------------------------------------------
$versionJsonPath = Join-Path $scriptDir "version.json"
if (-not (Test-Path $versionJsonPath)) {
    Write-Host "[LOI] Khong tim thay tap tin version.json tai thu muc goc!" -ForegroundColor Red
    exit 1
}

try {
    $versionJsonRaw = Get-Content -Path $versionJsonPath -Raw -Encoding UTF8
    $versionObj = $versionJsonRaw | ConvertFrom-Json
    $appVersion = $versionObj.version
    if ([string]::IsNullOrWhiteSpace($appVersion)) {
        $appVersion = "1.0.0"
    }
} catch {
    Write-Host "[CANH BAO] Khong doc duoc version.json, su dung mac dinh 1.0.0" -ForegroundColor Yellow
    $appVersion = "1.0.0"
}

# -------------------------------------------------------------------
# Ham tu dong tich luy changelogs.json va sinh CHANGELOG.md
# -------------------------------------------------------------------
function Update-ChangelogsHistory {
    param (
        [string]$RootDir,
        [PSCustomObject]$CurrentVersionObj
    )

    $changelogsJsonPath = Join-Path $RootDir "changelogs.json"
    $changelogMdPath = Join-Path $RootDir "CHANGELOG.md"

    $curVer = $CurrentVersionObj.version
    if ([string]::IsNullOrWhiteSpace($curVer)) { $curVer = "1.0.0" }
    $curReleaseDate = $CurrentVersionObj.releaseDate
    if ([string]::IsNullOrWhiteSpace($curReleaseDate)) { $curReleaseDate = (Get-Date -Format "yyyy-MM-dd") }

    $curChangelog = @()
    if ($null -ne $CurrentVersionObj.changelog) {
        $curChangelog = @($CurrentVersionObj.changelog)
    }

    # 1. Doc hoac khoi tao danh sach lich su changelogs
    $historyList = [System.Collections.Generic.List[PSCustomObject]]::new()
    if (Test-Path $changelogsJsonPath) {
        try {
            $rawContent = Get-Content -Path $changelogsJsonPath -Raw -Encoding UTF8
            $parsed = $rawContent | ConvertFrom-Json
            if ($parsed -is [System.Array] -or $parsed -is [System.Collections.IEnumerable]) {
                foreach ($item in $parsed) {
                    $historyList.Add([PSCustomObject]@{
                        version = [string]$item.version
                        releaseDate = [string]$item.releaseDate
                        changelog = @($item.changelog)
                    })
                }
            } elseif ($null -ne $parsed) {
                $historyList.Add([PSCustomObject]@{
                    version = [string]$parsed.version
                    releaseDate = [string]$parsed.releaseDate
                    changelog = @($parsed.changelog)
                })
            }
        } catch {
            Write-Host "      [CANH BAO] Khong doc duoc changelogs.json cu, khoi tao moi." -ForegroundColor Yellow
        }
    }

    # 2. Kiem tra xem phien ban hien tai da ton tai trong danh sach chua
    $existingIndex = -1
    for ($i = 0; $i -lt $historyList.Count; $i++) {
        if ($historyList[$i].version -eq $curVer) {
            $existingIndex = $i
            break
        }
    }

    $currentEntry = [PSCustomObject]@{
        version = $curVer
        releaseDate = $curReleaseDate
        changelog = $curChangelog
    }

    if ($existingIndex -ge 0) {
        $historyList[$existingIndex] = $currentEntry
        Write-Host "      [OK] Da cap nhat changelog cho phien ban v$curVer trong changelogs.json" -ForegroundColor Green
    } else {
        $historyList.Insert(0, $currentEntry)
        Write-Host "      [OK] Da them phien ban moi v$curVer vao dau changelogs.json" -ForegroundColor Green
    }

    # 3. Ghi ra changelogs.json (dinh dang JSON Pretty UTF8)
    $jsonOutput = $historyList | ConvertTo-Json -Depth 10
    Set-Content -Path $changelogsJsonPath -Value $jsonOutput -Encoding UTF8

    # 4. Tu dong sinh file CHANGELOG.md chuan Markdown
    $mdLines = [System.Collections.Generic.List[string]]::new()
    $mdLines.Add("# Nhat Ky Thay Doi (Changelog)")
    $mdLines.Add("")
    $mdLines.Add("Tat ca cac thay doi va ban cap nhat dang chu y cua du an **Omnisave** duoc ghi lai tai tai lieu nay.")
    $mdLines.Add("")

    foreach ($entry in $historyList) {
        $mdLines.Add("## [v$($entry.version)] - $($entry.releaseDate)")
        if ($entry.changelog -and $entry.changelog.Count -gt 0) {
            foreach ($line in $entry.changelog) {
                $mdLines.Add("- $line")
            }
        } else {
            $mdLines.Add("- Toi uu hoa he thong va sua loi.")
        }
        $mdLines.Add("")
    }

    Set-Content -Path $changelogMdPath -Value ($mdLines -join "`r`n") -Encoding UTF8
    Write-Host "      [OK] Da tao/dong bo file CHANGELOG.md thanh cong!" -ForegroundColor Green

    return @{
        ChangelogsJson = $changelogsJsonPath
        ChangelogMd = $changelogMdPath
    }
}

$defaultPublishDir = Join-Path $scriptDir "publish"

Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host "         Omnisave - Build & Package Distribution Script           " -ForegroundColor Cyan
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "[THONG TIN] Phien ban ung dung (tu version.json): v$appVersion" -ForegroundColor Green
Write-Host ""

if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    if ($Force -or $NonInteractive -or [Console]::IsInputRedirected) {
        $publishDir = $defaultPublishDir
    } else {
        $inputPath = Read-Host "Nhap duong dan xuat ban tuy chon (Nhan Enter de dung mac dinh: $defaultPublishDir)"
        if ([string]::IsNullOrWhiteSpace($inputPath)) {
            $publishDir = $defaultPublishDir
        } else {
            $cleanPath = $inputPath.Trim().Trim('"').Trim("'")
            $publishDir = [System.IO.Path]::GetFullPath($cleanPath)
        }
    }
} else {
    $publishDir = [System.IO.Path]::GetFullPath($PublishPath.Trim().Trim('"').Trim("'"))
}

Write-Host ""
Write-Host "-------------------------------------------------------------------" -ForegroundColor Yellow
Write-Host "XAC NHAN THONG SO DONG GOI:" -ForegroundColor Yellow
Write-Host " - Phien ban se dong goi: v$appVersion" -ForegroundColor Cyan
Write-Host " - Thu muc dich xuat ban: $publishDir" -ForegroundColor Cyan
Write-Host "-------------------------------------------------------------------" -ForegroundColor Yellow

if (-not ($Force -or $NonInteractive -or [Console]::IsInputRedirected)) {
    $confirm = Read-Host "Ban co chac chan muon tien hanh dong goi khong? (Y/n)"
    if (-not [string]::IsNullOrWhiteSpace($confirm) -and ($confirm -match '^[Nn]')) {
        Write-Host ""
        Write-Host "[DA HUY] Da huy tien trinh dong goi ung dung theo yeu cau." -ForegroundColor Gray
        exit 0
    }
}

Write-Host ""
Write-Host "[1/6] Dong bo phien ban v$appVersion vao Directory.Build.props..." -ForegroundColor Yellow
$propsPath = Join-Path $scriptDir "Directory.Build.props"
$nowStr = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$propsContent = @"
<Project>
  <PropertyGroup>
    <!-- Tu dong doc phien ban tu version.json lam Single Source of Truth ($nowStr) -->
    <_VersionJsonPath>`$([System.IO.Path]::Combine('`$(MSBuildThisFileDirectory)', 'version.json'))</_VersionJsonPath>
    <_VersionJsonRaw Condition="Exists('`$(_VersionJsonPath)')">`$([System.IO.File]::ReadAllText('`$(_VersionJsonPath)'))</_VersionJsonRaw>
    <_VersionFromJson Condition="'`$(_VersionJsonRaw)' != ''">`$([System.Text.RegularExpressions.Regex]::Match('`$(_VersionJsonRaw)', '`"version`"\s*:\s*`"([^`"]+)`"').Groups[1].Value)</_VersionFromJson>

    <Version Condition="'`$(_VersionFromJson)' != ''">`$(_VersionFromJson)</Version>
    <Version Condition="'`$(_VersionFromJson)' == ''">$appVersion</Version>
    <ApplicationDisplayVersion>`$(Version)</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
    <AssemblyVersion>`$(Version).0</AssemblyVersion>
    <FileVersion>`$(Version).0</FileVersion>
    <InformationalVersion>`$(Version)</InformationalVersion>
    <Company>Omnisave</Company>
    <Product>Omnisave</Product>
  </PropertyGroup>
</Project>
"@
Set-Content -Path $propsPath -Value $propsContent -Encoding UTF8
Write-Host "      [OK] Da cap nhat Directory.Build.props thanh cong!" -ForegroundColor Green

# Dong bo thong tin phan mem tu config/info.json vao AppBuildInfo.cs
$syncScript = Join-Path $scriptDir "sync_build_info.ps1"
if (Test-Path $syncScript) {
    & $syncScript -RootDir $scriptDir
}

Write-Host ""
Write-Host "[2/6] Khoi tao va chuan bi cau truc thu muc sach se..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    # Don dep cac file cu neu da ton tai
    Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

$appDir = Join-Path $publishDir "app"
$dataDir = Join-Path $publishDir "data"
$logsDir = Join-Path $dataDir "logs"
$configDir = Join-Path $dataDir "config"
$databaseDir = Join-Path $dataDir "database"
$backupsDir = Join-Path $dataDir "backups"
$coversDir = Join-Path $dataDir "covers"
$revertsDir = Join-Path $dataDir "reverts"
$tempDir = Join-Path $dataDir "temp"
$autoUpdateDir = Join-Path $tempDir "auto_update"

@($publishDir, $appDir, $dataDir, $logsDir, $configDir, $databaseDir, $backupsDir, $coversDir, $revertsDir, $tempDir, $autoUpdateDir) | ForEach-Object {
    if (-not (Test-Path $_)) { New-Item -ItemType Directory -Path $_ -Force | Out-Null }
}

$cfgFile = Join-Path $configDir "appconfig.json"
if (-not (Test-Path $cfgFile)) {
    Set-Content -Path $cfgFile -Value "{`n}" -Encoding UTF8
}

Write-Host ""
Write-Host "[3/6] Bien dich Core App (.NET 10 MAUI Release) vao app/..." -ForegroundColor Yellow
$uiProj = Join-Path $scriptDir "SaveGameBackup.UI\SaveGameBackup.UI.csproj"
dotnet publish $uiProj -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -o $appDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "[THAT BAI] Loi bien dich SaveGameBackup.UI!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "[4/6] Don dep cac thu muc ngon ngu la va tap tin build thua..." -ForegroundColor Yellow
Get-ChildItem -Path $appDir -Directory | ForEach-Object {
    $name = $_.Name
    $isResourceLang = (Get-ChildItem -Path $_.FullName -Include "*.mui", "*.resources.dll" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0
    if ($isResourceLang -and ($name -notin @('vi', 'vi-VN', 'en-us', 'en-GB'))) {
        Remove-Item -Path $_.FullName -Recurse -Force
    }
}
Get-ChildItem -Path $appDir -Include "*.pdb", "package.json", "package-lock.json", "AboutAssets.txt", "*.staticwebassets.endpoints.json" -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force

$guideHtml = Join-Path $scriptDir "GoogleDrive_Setup_Guide.html"
if (Test-Path $guideHtml) {
    Copy-Item $guideHtml -Destination $appDir -Force
    Write-Host "      [OK] Da sao chep GoogleDrive_Setup_Guide.html vao app/" -ForegroundColor Green
}

$guideMd = Join-Path $scriptDir "GOOGLE_DRIVE_SETUP_GUIDE.md"
if (Test-Path $guideMd) {
    Copy-Item $guideMd -Destination $appDir -Force
}

$manifestYaml = Join-Path $scriptDir "manifest.yaml"
if (Test-Path $manifestYaml) {
    Copy-Item $manifestYaml -Destination $databaseDir -Force
    Write-Host "      [OK] Da sao chep manifest.yaml vao data/database/" -ForegroundColor Green
}

if (Test-Path $versionJsonPath) {
    Copy-Item $versionJsonPath -Destination $publishDir -Force
    Write-Host "      [OK] Da sao chep version.json vao thu muc xuat ban" -ForegroundColor Green
}

# Cap nhat lich su changelogs va sao chep vao thu muc xuat ban
$changelogFiles = Update-ChangelogsHistory -RootDir $scriptDir -CurrentVersionObj $versionObj
if (Test-Path $changelogFiles.ChangelogsJson) {
    Copy-Item $changelogFiles.ChangelogsJson -Destination $publishDir -Force
    Write-Host "      [OK] Da sao chep changelogs.json vao thu muc xuat ban" -ForegroundColor Green
}
if (Test-Path $changelogFiles.ChangelogMd) {
    Copy-Item $changelogFiles.ChangelogMd -Destination $publishDir -Force
    Write-Host "      [OK] Da sao chep CHANGELOG.md vao thu muc xuat ban" -ForegroundColor Green
}

$publishReadme = Join-Path $scriptDir "PUBLISH_README.md"
if (Test-Path $publishReadme) {
    Copy-Item $publishReadme -Destination (Join-Path $publishDir "README.md") -Force
    Write-Host "      [OK] Da sao chep PUBLISH_README.md vao thu muc xuat ban (README.md)" -ForegroundColor Green
}

Write-Host ""
Write-Host "[5/6] Bien dich Root Launcher (Omnisave.exe) vao thu muc xuat ban..." -ForegroundColor Yellow
$launcherProj = Join-Path $scriptDir "SaveGameBackup.Launcher\SaveGameBackup.Launcher.csproj"
dotnet publish $launcherProj -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "[THAT BAI] Loi bien dich SaveGameBackup.Launcher!" -ForegroundColor Red
    exit $LASTEXITCODE
}

$launcherPdb = Join-Path $publishDir "Omnisave.pdb"
if (Test-Path $launcherPdb) {
    Remove-Item $launcherPdb -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "[6/6] Kiem tra toan ven cau truc goi xuat ban..." -ForegroundColor Yellow
$rootExe = Join-Path $publishDir "Omnisave.exe"
$coreExe = Join-Path $appDir "Omnisave.exe"

if (-not (Test-Path $rootExe)) {
    Write-Host "[LOI] Khong tim thay Root Launcher Omnisave.exe!" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $coreExe)) {
    Write-Host "[LOI] Khong tim thay Core App app\Omnisave.exe!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "===================================================================" -ForegroundColor Green
Write-Host "[THANH CONG] Qua trinh dong goi ung dung hoan tat!" -ForegroundColor Green
Write-Host "Thong tin phien ban: v$appVersion" -ForegroundColor Cyan
Write-Host "Duong dan phat hanh: $publishDir" -ForegroundColor Cyan
Write-Host "Cau truc thu muc da duoc to chuc cuc ky gon gang:" -ForegroundColor Cyan
Write-Host " - Omnisave.exe           (Launcher khoi chay ung dung)" -ForegroundColor Cyan
Write-Host " - data/                   (CSDL, Backups, Logs, Covers, Reverts, Temp, Config)" -ForegroundColor Cyan
Write-Host " - app/                    (Toan bo binaries, assets va huong dan)" -ForegroundColor Cyan
Write-Host " - version.json            (Thong tin phien ban auto-update)" -ForegroundColor Cyan
Write-Host " - changelogs.json         (Lich su tat ca cac phien ban)" -ForegroundColor Cyan
Write-Host " - README.md               (Huong dan su dung danh cho nguoi dung)" -ForegroundColor Cyan
Write-Host " - CHANGELOG.md            (Nhat ky cap nhat Markdown)" -ForegroundColor Cyan
Write-Host "===================================================================" -ForegroundColor Green
Write-Host ""

if (-not ($Force -or $NonInteractive -or [Console]::IsInputRedirected)) {
    Invoke-Item $publishDir
}
