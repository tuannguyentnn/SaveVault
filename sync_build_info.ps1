param(
    [string]$RootDir = ""
)

if ([string]::IsNullOrWhiteSpace($RootDir)) {
    $RootDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($RootDir)) {
        $RootDir = (Get-Location).Path
    }
}

$infoJsonPath = Join-Path $RootDir "config\info.json"
$appBuildInfoCsPath = Join-Path $RootDir "SaveGameBackup.Core\Constants\AppBuildInfo.cs"
$versionJsonPath = Join-Path $RootDir "version.json"

if (-not (Test-Path $infoJsonPath)) {
    Write-Warning "Khong tim thay $infoJsonPath"
    exit 0
}

try {
    $infoRaw = Get-Content -Path $infoJsonPath -Raw -Encoding UTF8
    $info = $infoRaw | ConvertFrom-Json

    # Doc phien ban va ngay build tu version.json (Single Source of Truth nhu logic cu - khong lay tu info.json)
    $version = "1.0.0"
    $buildDate = (Get-Date -Format "yyyy-MM-dd")

    if (Test-Path $versionJsonPath) {
        try {
            $verRaw = Get-Content -Path $versionJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace($verRaw.version)) {
                $version = [string]$verRaw.version
            }
            if (-not [string]::IsNullOrWhiteSpace($verRaw.releaseDate)) {
                $buildDate = [string]$verRaw.releaseDate
            }
        } catch {
            Write-Warning "Khong doc duoc version.json: $_"
        }
    }

    $appName = [string]$info.appName
    $appTitle = [string]$info.appTitle
    $author = [string]$info.author
    $description = [string]$info.description
    $license = [string]$info.license

    $framework = [string]$info.techStack.framework
    $database = [string]$info.techStack.database
    $styling = [string]$info.techStack.styling
    $cloud = [string]$info.techStack.cloud
    $manifestDb = [string]$info.techStack.manifestDb

    $github = [string]$info.links.github
    $issues = [string]$info.links.issues
    $releases = [string]$info.links.releases
    $guide = [string]$info.links.guide

    $email = [string]$info.support.email
    $community = [string]$info.support.community
    $telegram = [string]$info.support.telegram

    $featuresLines = @()
    if ($info.features) {
        foreach ($f in $info.features) {
            $featuresLines += "        `"$([string]$f)`","
        }
    }
    $featuresBlock = $featuresLines -join "`r`n"

    $csCode = @"
using System.Collections.Generic;

namespace SaveGameBackup.Core.Constants;

/// <summary>
/// Chứa thông tin ứng dụng được nạp tĩnh trực tiếp vào mã nguồn lúc build.
/// Version và BuildDate được tự động đọc từ version.json (Single Source of Truth).
/// Các thông tin mô tả và hỗ trợ được nạp từ config/info.json.
/// Giúp ứng dụng khởi chạy tức thì 0ms (Zero Disk I/O) mà không cần đọc ổ đĩa mỗi khi mở app.
/// </summary>
public static class AppBuildInfo
{
    public const string AppName = "$appName";
    public const string AppTitle = "$appTitle";
    public const string Version = "$version";
    public const string BuildDate = "$buildDate";
    public const string Author = "$author";
    public const string Description = "$description";
    public const string License = "$license";

    public static class TechStack
    {
        public const string Framework = "$framework";
        public const string Database = "$database";
        public const string Styling = "$styling";
        public const string Cloud = "$cloud";
        public const string ManifestDb = "$manifestDb";
    }

    public static class Links
    {
        public const string GitHub = "$github";
        public const string Issues = "$issues";
        public const string Releases = "$releases";
        public const string Guide = "$guide";
    }

    public static class Support
    {
        public const string Email = "$email";
        public const string Community = "$community";
        public const string Telegram = "$telegram";
    }

    public static readonly IReadOnlyList<string> Features = new[]
    {
$featuresBlock
    };
}
"@

    Set-Content -Path $appBuildInfoCsPath -Value $csCode -Encoding UTF8
    Write-Host "[OK] Da dong bo AppBuildInfo.cs (Version: $version, BuildDate: $buildDate tu version.json) thanh cong." -ForegroundColor Green
} catch {
    Write-Warning "Loi khi dong bo AppBuildInfo.cs: $_"
}
