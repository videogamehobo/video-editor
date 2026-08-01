param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\HighlightForge.App\HighlightForge.App.csproj"
$output = Join-Path $PSScriptRoot "..\artifacts\$Runtime"
dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true --output $output
Write-Host "Published HighlightForge to $output"
