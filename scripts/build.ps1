param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "SideBySideSync Build Script" -ForegroundColor Cyan
Write-Host "============================`n" -ForegroundColor Cyan

# Function to install .NET SDK via winget
function Install-DotNetSDK {
    Write-Host "`nAttempting to install .NET 8 SDK via winget..." -ForegroundColor Yellow
    
    # Check if winget is available
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host "ERROR: winget not found!" -ForegroundColor Red
        Write-Host "Please manually install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        return $false
    }
    
    Write-Host "Installing Microsoft.DotNet.SDK.8..." -ForegroundColor Yellow
    $result = winget install Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
    
    if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335189) {
        Write-Host "Installation completed. Refreshing environment..." -ForegroundColor Green
        # Refresh PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
        Start-Sleep -Seconds 2
        return $true
    } else {
        Write-Host "ERROR: winget installation failed with exit code $LASTEXITCODE" -ForegroundColor Red
        Write-Host "Please manually install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        return $false
    }
}

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow
$needsInstall = $false
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue

if (-not $dotnetCmd) {
    Write-Host "dotnet command not found in PATH" -ForegroundColor Yellow
    $needsInstall = $true
} else {
    Write-Host "Found dotnet at: $($dotnetCmd.Source)" -ForegroundColor Gray
    
    # Check .NET version and SDKs
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Check if SDK directory exists
        $sdkPath = Join-Path (Split-Path $dotnetCmd.Source) "sdk"
        if (-not (Test-Path $sdkPath)) {
            Write-Host ".NET SDK not installed (only runtime found)" -ForegroundColor Yellow
            $needsInstall = $true
        } else {
            Write-Host "ERROR: .NET SDK not working properly!" -ForegroundColor Red
            Write-Host "Please manually install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
            exit 1
        }
    } else {
        Write-Host "Found .NET SDK version: $dotnetVersion" -ForegroundColor Green
        
        # Check if .NET 8 SDK is available
        $sdks = & dotnet --list-sdks 2>&1
        if ($LASTEXITCODE -eq 0 -and $sdks) {
            $hasNet8 = $sdks | Where-Object { $_ -match '^8\.' }
            if (-not $hasNet8) {
                Write-Host ".NET 8 SDK not found" -ForegroundColor Yellow
                Write-Host "Installed SDKs:" -ForegroundColor Gray
                $sdks | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
                $needsInstall = $true
            }
        } else {
            Write-Host "No .NET SDKs found" -ForegroundColor Yellow
            $needsInstall = $true
        }
    }
}

# Install if needed
if ($needsInstall) {
    if (-not (Install-DotNetSDK)) {
        exit 1
    }
    
    # Verify installation
    Write-Host "`nVerifying installation..." -ForegroundColor Yellow
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) {
        Write-Host "ERROR: dotnet still not found after installation!" -ForegroundColor Red
        Write-Host "You may need to restart your terminal or computer." -ForegroundColor Yellow
        exit 1
    }
    
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: .NET SDK still not working after installation!" -ForegroundColor Red
        Write-Host "You may need to restart your terminal or computer." -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Successfully installed .NET SDK version: $dotnetVersion" -ForegroundColor Green
}

Write-Host "Prerequisites OK`n" -ForegroundColor Green

# Navigate to solution directory
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Split-Path -Parent $scriptPath
Push-Location $solutionRoot

try {
    # Restore dependencies
    Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
    dotnet restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }
    Write-Host "Restore completed`n" -ForegroundColor Green

    # Build project
    Write-Host "Building SideBySideSync ($Configuration)..." -ForegroundColor Yellow
    dotnet build src/SideBySideSync/SideBySideSync.csproj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
    
    Write-Host "`nBuild completed successfully!" -ForegroundColor Green
    Write-Host "Output: src/SideBySideSync/bin/$Configuration/net8.0/" -ForegroundColor Cyan
}
catch {
    Write-Host "`nERROR: Build failed - $_" -ForegroundColor Red
    Pop-Location
    exit 1
}
finally {
    Pop-Location
}
