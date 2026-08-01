param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$SkipInstaller,
    [switch]$SkipStartup,
    [switch]$SkipInstallLifecycle
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishDirectory = Join-Path $repositoryRoot "artifacts\$Runtime"
$requiredFiles = @(
    "HighlightForge.App.exe",
    "HighlightForge.App.dll",
    "worker\HighlightForge.Worker.exe",
    "worker\HighlightForge.Worker.dll",
    "THIRD-PARTY-NOTICES.md",
    "tools\ffmpeg\ffmpeg.exe",
    "tools\ffmpeg\ffprobe.exe",
    "tools\ffmpeg\FFMPEG-LICENSE.txt"
)
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package verification failed: missing $relativePath" }
}

& (Join-Path $publishDirectory "tools\ffmpeg\ffmpeg.exe") -hide_banner -version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Bundled FFmpeg did not start." }
& (Join-Path $publishDirectory "tools\ffmpeg\ffprobe.exe") -hide_banner -version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Bundled FFprobe did not start." }
$workerHealth = & (Join-Path $publishDirectory "worker\HighlightForge.Worker.exe") --health
if ($LASTEXITCODE -ne 0 -or $workerHealth -notmatch '"status":"ready"') { throw "Packaged analysis worker did not pass its health check." }

function Assert-AppStarts([string]$Executable) {
    $process = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Seconds 3
        if ($process.HasExited) { throw "Packaged HighlightForge exited during startup with code $($process.ExitCode)." }
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
            $process.WaitForExit()
        }
    }
}

if (-not $SkipStartup) { Assert-AppStarts (Join-Path $publishDirectory "HighlightForge.App.exe") }

if (-not $SkipInstaller) {
    $installer = Join-Path $repositoryRoot "artifacts\installer\HighlightForge-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Package verification failed: missing installer $installer" }
    if ((Get-Item -LiteralPath $installer).Length -lt 1MB) { throw "Package verification failed: installer is unexpectedly small." }
    if (-not $SkipInstallLifecycle) {
        $smokeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\install-smoke-$([Guid]::NewGuid().ToString('N'))"))
        $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts")) + [IO.Path]::DirectorySeparatorChar
        if (-not $smokeRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe installer smoke-test path." }
        $install = Start-Process -FilePath $installer -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOICONS", "/DIR=$smokeRoot") -PassThru -Wait
        if ($install.ExitCode -ne 0) { throw "Installer smoke test failed with exit code $($install.ExitCode)." }
        $installedApp = Join-Path $smokeRoot "HighlightForge.App.exe"
        $uninstaller = Join-Path $smokeRoot "unins000.exe"
        if (-not (Test-Path -LiteralPath $installedApp -PathType Leaf) -or -not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            throw "Installer smoke test did not install the application and uninstaller."
        }
        if (-not $SkipStartup) { Assert-AppStarts $installedApp }
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -PassThru -Wait
        if ($uninstall.ExitCode -ne 0) { throw "Uninstaller smoke test failed with exit code $($uninstall.ExitCode)." }
        if (Test-Path -LiteralPath $smokeRoot) { throw "Uninstaller smoke test left the installation directory behind: $smokeRoot" }
    }
}
Write-Host "Verified HighlightForge $Version package, separate analysis worker, bundled LGPL media tools, startup, and installer lifecycle."
