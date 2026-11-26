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

Write-Host "`nTamamlandi! Tekil dosya & SHA-256:" -ForegroundColor Green
$exePath = "$outDir\KochanSentinel.exe"
$hash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  KochanSentinel.exe" | Set-Content -Path "$outDir\SHA256SUMS.txt" -Encoding ASCII
Get-Item $exePath | Select-Object FullName, Length, LastWriteTime
Write-Host "SHA256: $hash" -ForegroundColor Cyan
