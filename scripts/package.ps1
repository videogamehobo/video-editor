param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [string]$FfmpegDirectory = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repositoryRoot "src\HighlightForge.App\HighlightForge.App.csproj"
$workerProject = Join-Path $repositoryRoot "src\HighlightForge.Worker\HighlightForge.Worker.csproj"
$output = Join-Path $repositoryRoot "artifacts\$Runtime"
$workerOutput = Join-Path $output "worker"
$ffmpegOutput = Join-Path $output "tools\ffmpeg"

function Find-ExecutableDirectory([string]$ExecutableName) {
    foreach ($target in @([EnvironmentVariableTarget]::User, [EnvironmentVariableTarget]::Machine)) {
        $registeredPath = [Environment]::GetEnvironmentVariable("Path", $target)
        if ([string]::IsNullOrWhiteSpace($registeredPath)) { continue }
        foreach ($directory in $registeredPath.Split([IO.Path]::PathSeparator, [StringSplitOptions]::RemoveEmptyEntries)) {
            $candidate = Join-Path $directory.Trim() $ExecutableName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetDirectoryName($candidate) }
        }
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($FfmpegDirectory)) { $FfmpegDirectory = Find-ExecutableDirectory "ffmpeg.exe" }
if ([string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
    throw "An LGPL FFmpeg directory containing ffmpeg.exe and ffprobe.exe is required. Pass -FfmpegDirectory or install BtbN.FFmpeg.LGPL.8.1."
}
$FfmpegDirectory = [IO.Path]::GetFullPath($FfmpegDirectory)
foreach ($requiredFile in @("ffmpeg.exe", "ffprobe.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $FfmpegDirectory $requiredFile) -PathType Leaf)) {
        throw "Missing $requiredFile in $FfmpegDirectory."
    }
}

dotnet restore $project --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "Application runtime restore failed with exit code $LASTEXITCODE." }
dotnet restore $workerProject --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "Worker runtime restore failed with exit code $LASTEXITCODE." }
dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true --output $output --no-restore /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE." }
dotnet publish $workerProject --configuration $Configuration --runtime $Runtime --self-contained true --output $workerOutput --no-restore /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed with exit code $LASTEXITCODE." }
New-Item -ItemType Directory -Path $ffmpegOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $FfmpegDirectory "ffmpeg.exe") -Destination $ffmpegOutput -Force
Copy-Item -LiteralPath (Join-Path $FfmpegDirectory "ffprobe.exe") -Destination $ffmpegOutput -Force
$ffmpegLicense = Join-Path ([IO.Path]::GetDirectoryName($FfmpegDirectory)) "LICENSE.txt"
if (-not (Test-Path -LiteralPath $ffmpegLicense -PathType Leaf)) { throw "The FFmpeg LICENSE.txt file is required beside the bin directory." }
Copy-Item -LiteralPath $ffmpegLicense -Destination (Join-Path $ffmpegOutput "FFMPEG-LICENSE.txt") -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $output -Force
Write-Host "Published self-contained HighlightForge with separate LGPL FFmpeg tools to $output"

if (-not $SkipInstaller) {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($iscc)) { throw "Inno Setup 6 is required to build the installer. Install JRSoftware.InnoSetup or pass -SkipInstaller." }
    $installerOutput = Join-Path $repositoryRoot "artifacts\installer"
    New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
    & $iscc "/DPublishDir=$output" "/DOutputDir=$installerOutput" "/DAppVersion=$Version" (Join-Path $repositoryRoot "installer\HighlightForge.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
    Write-Host "Built installer in $installerOutput"
}
