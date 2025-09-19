# PowerShell test runner script for Windows

Write-Host "CS:GO Skin Statistics - Test Runner" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan

$ErrorActionPreference = "Stop"
$success = $true

# Function to run command and check result
function Invoke-TestCommand {
    param(
        [string]$Command,
        [string]$Description
    )

    Write-Host "`n$Description..." -ForegroundColor Yellow

    try {
        Invoke-Expression $Command
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAILED: $Description" -ForegroundColor Red
            return $false
        } else {
            Write-Host "PASSED: $Description" -ForegroundColor Green
            return $true
        }
    } catch {
        Write-Host "ERROR: $Description - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Check if dependencies are installed
Write-Host "`nChecking dependencies..." -ForegroundColor Yellow

if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: .NET CLI not found. Please install .NET 9.0 SDK" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command "npm" -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: npm not found. Please install Node.js" -ForegroundColor Red
    exit 1
}

# Install npm dependencies if needed
if (-not (Test-Path "node_modules")) {
    Write-Host "Installing npm dependencies..." -ForegroundColor Yellow
    npm install
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to install npm dependencies" -ForegroundColor Red
        exit 1
    }
}

# Run .NET backend tests
Write-Host "`n" + "="*50 -ForegroundColor Cyan
Write-Host "Running Backend Tests (.NET)" -ForegroundColor Cyan
Write-Host "="*50 -ForegroundColor Cyan

if (Test-Path "csgoSkinStatistics.Tests") {
    if (-not (Invoke-TestCommand "dotnet test csgoSkinStatistics.Tests --verbosity normal --logger console" "Backend unit tests")) {
        $success = $false
    }
} else {
    Write-Host "WARNING: Backend test project not found" -ForegroundColor Yellow
}

# Run JavaScript frontend tests
Write-Host "`n" + "="*50 -ForegroundColor Cyan
Write-Host "Running Frontend Tests (JavaScript)" -ForegroundColor Cyan
Write-Host "="*50 -ForegroundColor Cyan

if (-not (Invoke-TestCommand "npm test -- --passWithNoTests" "Frontend unit tests")) {
    $success = $false
}

# Run linting
Write-Host "`n" + "="*50 -ForegroundColor Cyan
Write-Host "Running Code Quality Checks" -ForegroundColor Cyan
Write-Host "="*50 -ForegroundColor Cyan

if (Test-Path "wwwroot/*.js") {
    if (-not (Invoke-TestCommand "npm run lint:js" "JavaScript linting")) {
        $success = $false
    }
}

if (Test-Path "wwwroot/*.css") {
    if (-not (Invoke-TestCommand "npm run lint:css" "CSS linting")) {
        $success = $false
    }
}

# Build the application
Write-Host "`n" + "="*50 -ForegroundColor Cyan
Write-Host "Building Application" -ForegroundColor Cyan
Write-Host "="*50 -ForegroundColor Cyan

if (-not (Invoke-TestCommand "dotnet build --configuration Release" "Application build")) {
    $success = $false
}

# Summary
Write-Host "`n" + "="*50 -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "="*50 -ForegroundColor Cyan

if ($success) {
    Write-Host "All tests passed! ✅" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Some tests failed! ❌" -ForegroundColor Red
    exit 1
}