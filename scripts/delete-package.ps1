#!/usr/bin/env pwsh
# Cross-platform script to delete Caching.NET package version from GitHub Packages
# Works on both Windows and macOS/Linux
# Usage: pwsh scripts/delete-package.ps1 [VERSION] [GITHUB_PAT]
#        or: powershell scripts/delete-package.ps1 [VERSION] [GITHUB_PAT]
#        or: ./scripts/delete-package.ps1 [VERSION] [GITHUB_PAT] (if executable)
#
# Features:
# - Checks if package version exists before attempting deletion
# - Requires GitHub PAT with 'delete:packages' permission

param(
    [Parameter(Mandatory = $true)]
    [string]$Version = "",
    [string]$GitHubPAT = $env:GITHUB_PAT
)

$ErrorActionPreference = "Stop"

# Get repository root directory (cross-platform)
try {
    $RepoRoot = (git rev-parse --show-toplevel 2>&1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepoRoot)) {
        throw "Git command failed"
    }
} catch {
    # Fallback: assume script is in scripts/ directory, go up one level
    if ($PSScriptRoot) {
        $RepoRoot = Split-Path -Parent $PSScriptRoot
    } else {
        # If PSScriptRoot is not available, use current location
        $currentDir = (Get-Location).Path
        # Check if we're in scripts directory
        if ((Split-Path -Leaf $currentDir) -eq "scripts") {
            $RepoRoot = Split-Path -Parent $currentDir
        } else {
            $RepoRoot = $currentDir
        }
    }
}

# Configuration
$PackageName = "Caching.NET"
$Namespace = $env:GITHUB_NAMESPACE ?? "baps-apps"
$RepoName = "caching-net"

# Get GitHub PAT from argument or environment variable
if ([string]::IsNullOrEmpty($GitHubPAT)) {
    Write-Host "Error: GitHub Personal Access Token required" -ForegroundColor Red
    Write-Host "Usage: pwsh scripts/delete-package.ps1 [VERSION] [GITHUB_PAT]" -ForegroundColor Yellow
    Write-Host "Or set GITHUB_PAT environment variable" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Note: Your PAT needs 'delete:packages' permission" -ForegroundColor Yellow
    exit 1
}

Write-Host "Deleting Caching.NET v$Version from GitHub Packages" -ForegroundColor Yellow
Write-Host ""

function Test-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName
    )

    try {
        $headers = @{
            "Authorization"        = "Bearer $Token"
            "Accept"               = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }

        $versionsUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions"
        $versionsResponse = Invoke-RestMethod -Uri $versionsUrl -Method Get -Headers $headers -ErrorAction Stop

        $existingVersion = $versionsResponse | Where-Object { $_.name -eq $PackageVersion } | Select-Object -First 1

        if ($existingVersion) {
            return @{
                Exists      = $true
                VersionId   = $existingVersion.id
                VersionName = $existingVersion.name
            }
        } else {
            return @{
                Exists      = $false
                VersionId   = $null
                VersionName = $null
            }
        }
    } catch {
        Write-Host "  Could not check package version via API: $_" -ForegroundColor Yellow
        return @{
            Exists      = $null
            VersionId   = $null
            VersionName = $null
        }
    }
}

function Remove-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName,
        [string]$VersionId
    )

    try {
        $headers = @{
            "Authorization"        = "Bearer $Token"
            "Accept"               = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }

        $deleteUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions/$VersionId"
        Invoke-RestMethod -Uri $deleteUrl -Method Delete -Headers $headers -ErrorAction Stop
        Write-Host "  Package version deleted successfully" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "  Could not delete package via API: $_" -ForegroundColor Yellow
        Write-Host "  Note: Your PAT needs 'delete:packages' permission" -ForegroundColor Yellow
        return $false
    }
}

Write-Host "Step 1: Checking if package version exists..." -ForegroundColor Yellow
$versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $Version -Token $GitHubPAT -OrgName $Namespace

if ($versionCheck.Exists -eq $true) {
    Write-Host "  Package version $Version found" -ForegroundColor Green
    Write-Host ""

    Write-Host "Step 2: Deleting package version..." -ForegroundColor Yellow
    $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $Version -Token $GitHubPAT -OrgName $Namespace -VersionId $versionCheck.VersionId

    if ($deleted) {
        Write-Host ""
        Write-Host "Package version $Version deleted successfully!" -ForegroundColor Green
        Write-Host ""
        Write-Host "View your packages at: https://github.com/$Namespace/$RepoName/packages"
        Write-Host ""
    } else {
        Write-Host ""
        Write-Host "Failed to delete package version $Version" -ForegroundColor Red
        Write-Host ""
        Write-Host "To manually delete the package:" -ForegroundColor Yellow
        Write-Host "  1. Go to https://github.com/$Namespace/$RepoName/packages" -ForegroundColor Yellow
        Write-Host "  2. Select the $PackageName package" -ForegroundColor Yellow
        Write-Host "  3. Delete version $Version" -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
} elseif ($versionCheck.Exists -eq $false) {
    Write-Host "  Package version $Version does not exist" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "No action needed - package version not found" -ForegroundColor Green
    Write-Host ""
    exit 0
} else {
    Write-Host "  Could not determine if package version exists" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Error: Unable to check package version status" -ForegroundColor Red
    Write-Host "Please verify:" -ForegroundColor Yellow
    Write-Host "  1. Your GitHub PAT has 'read:packages' permission" -ForegroundColor Yellow
    Write-Host "  2. The package name and organization are correct" -ForegroundColor Yellow
    Write-Host "  3. Your network connection is working" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

