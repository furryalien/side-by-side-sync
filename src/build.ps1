param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building SideBySideSync ($Configuration)..."
dotnet restore
dotnet build src/SideBySideSync/SideBySideSync.csproj -c $Configuration --no-restore
Write-Host "Build completed."
