param(
    [switch]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$outDir = Join-Path $PSScriptRoot "publish"
$scArg = if ($SelfContained) { "true" } else { "false" }

Write-Host "Publishing KOCHAN Sentinel Single-File EXE (SelfContained=$scArg) -> $outDir" -ForegroundColor Cyan
& $dotnet publish "$PSScriptRoot\src\KochanSentinel.App\KochanSentinel.App.csproj" `
    -c Release -r win-x64 --self-contained $scArg `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir

Write-Host "`nTamamlandi! Tekil dosya:" -ForegroundColor Green
Get-Item "$outDir\KochanSentinel.exe" | Select-Object FullName, Length, LastWriteTime
