param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $project = Join-Path $root "src\FreeSoundPad.App\FreeSoundPad.App.csproj"
    $output = Join-Path $root "dist"

    Write-Host "Publishing FreeSoundPad ($Configuration, $Runtime)..." -ForegroundColor Cyan
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $output

    $exe = Join-Path $output "FreeSoundPad.exe"
    if (-not (Test-Path $exe)) { throw "Publish failed: $exe not found" }

    # Documentation XML files from NuGet packages are not needed next to the portable exe.
    Get-ChildItem $output -Filter "*.xml" -File -ErrorAction SilentlyContinue | Remove-Item -Force

    # Ship the README next to the exe so the setup instructions are always at hand.
    $readme = Join-Path $root "README.md"
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $output "README.md") -Force }

    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Done: $exe ($size MB)" -ForegroundColor Green

    # Optional portable mode marker: create dist\portable.txt to store data next to the exe.
    Write-Host "Tip: create dist\portable.txt for portable mode (data stored in dist\Data)." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
