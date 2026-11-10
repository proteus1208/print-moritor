
$ErrorActionPreference = "Stop"

$Output = Join-Path $PSScriptRoot "Release"
New-Item -ItemType Directory -Force -Path $Output | Out-Null

Write-Host "Building PrintArchive..." -ForegroundColor Cyan

$Projects = @(
    "PrintArchive.Service",
    "PrintArchive.Agent",
    "PrintArchive.UI"
)

foreach ($Project in $Projects) {
    Write-Host "Publishing $Project..."

    dotnet publish `
        "$PSScriptRoot\src\$Project\$Project.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o "$Output"
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Project"
    }
}

Write-Host "Build completed: $Output" -ForegroundColor Green
