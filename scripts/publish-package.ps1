#!/usr/bin/env pwsh
# Cross-platform script to publish Caching.NET package to GitHub Packages
# Works on both Windows and macOS/Linux
# Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]
#        or: powershell scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]
#        or: ./scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] (if executable)
#
# Features:
# - Automatically deletes and republishes if package version already exists
# - Requires GitHub PAT with 'write:packages' and 'delete:packages' permissions

param(
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
        $currentDir = (Get-Location).Path
        if ((Split-Path -Leaf $currentDir) -eq "scripts") {
            $RepoRoot = Split-Path -Parent $currentDir
        } else {
            $RepoRoot = $currentDir
        }
    }
}

# Configuration (using Join-Path for cross-platform compatibility)
$ProjectPath = Join-Path $RepoRoot "src" "Caching.NET" "Caching.NET.csproj"
$PackageOutput = Join-Path $RepoRoot "nupkgs"
$Namespace = $env:GITHUB_NAMESPACE ?? "baps-apps"
$RepoName = "caching-net"
$SourceName = "github"
$PackageName = "Caching.NET"

# Track if version was provided as parameter
$versionProvidedAsParam = $PSBoundParameters.ContainsKey('Version') -and -not [string]::IsNullOrEmpty($PSBoundParameters['Version'])

# Get version from argument or .csproj file
if ([string]::IsNullOrEmpty($Version)) {
    if (-not (Test-Path $ProjectPath)) {
        Write-Host "Error: Project file not found at $ProjectPath" -ForegroundColor Red
        exit 1
    }

    $versionMatch = Select-String -Path $ProjectPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($versionMatch -and $versionMatch.Matches.Groups.Count -gt 1) {
        $Version = $versionMatch.Matches.Groups[1].Value.Trim()
    }

    if ([string]::IsNullOrEmpty($Version)) {
        Write-Host "Error: Could not extract version from .csproj file" -ForegroundColor Red
        Write-Host "Add <Version>1.0.0</Version> to Caching.NET.csproj or pass version: pwsh scripts/publish-package.ps1 1.0.0" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "No version specified, using version from .csproj: $Version" -ForegroundColor Yellow
}

# Get GitHub PAT from argument or environment variable
if ([string]::IsNullOrEmpty($GitHubPAT)) {
    Write-Host "Error: GitHub Personal Access Token required" -ForegroundColor Red
    Write-Host "Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]"
    Write-Host "Or set GITHUB_PAT environment variable"
    Write-Host ""
    Write-Host "Note: Your PAT needs both 'write:packages' and 'delete:packages' permissions"
    Write-Host "      to automatically overwrite existing package versions."
    exit 1
}

Write-Host "Publishing Caching.NET v$Version to GitHub Packages" -ForegroundColor Green
Write-Host ""

# Step 1: Authenticate
Write-Host "Step 1: Authenticating to GitHub Packages..." -ForegroundColor Yellow
$sourceUrl = "https://nuget.pkg.github.com/$Namespace/index.json"

$username = $null
if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) {
    if ($IsWindows) {
        $username = $env:USERNAME
    } else {
        $username = $env:USER
    }
} else {
    if ($env:OS -like "*Windows*" -or $env:USERNAME) {
        $username = $env:USERNAME
    } else {
        $username = $env:USER
    }
}

if ([string]::IsNullOrWhiteSpace($username)) {
    $username = $env:USERNAME ?? $env:USER ?? "github"
    Write-Warning "Could not determine username, using: $username"
}

try {
    dotnet nuget add source $sourceUrl `
        --name $SourceName `
        --username $username `
        --password $GitHubPAT `
        --store-password-in-clear-text 2>&1 | Out-Null
} catch {
    # Source might already exist
}

Write-Host "Authentication configured" -ForegroundColor Green
Write-Host ""

# Step 2: Update version in .csproj if version was provided as parameter
if ($versionProvidedAsParam) {
    Write-Host "Step 2: Updating version in project file..." -ForegroundColor Yellow
    try {
        $csprojContent = Get-Content $ProjectPath -Raw
        $originalContent = $csprojContent

        if ($csprojContent -match '<Version>([^<]+)</Version>') {
            $csprojContent = $csprojContent -replace '<Version>([^<]+)</Version>', "<Version>$Version</Version>"
        } else {
            # Insert Version into first PropertyGroup
            $csprojContent = $csprojContent -replace '(<PropertyGroup>\s*)', "`$1`n    <Version>$Version</Version>`n"
        }

        if ($csprojContent -ne $originalContent) {
            Set-Content -Path $ProjectPath -Value $csprojContent -NoNewline
            Write-Host "Version updated to $Version" -ForegroundColor Green
        }
    } catch {
        Write-Host "Warning: Could not update version in .csproj file: $_" -ForegroundColor Yellow
    }
    Write-Host ""
}

# Step 3: Build
Write-Host "Step 3: Building project..." -ForegroundColor Yellow
dotnet build $ProjectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "Build completed" -ForegroundColor Green
Write-Host ""

# Step 4: Pack
Write-Host "Step 4: Creating NuGet package..." -ForegroundColor Yellow
if (-not (Test-Path $PackageOutput)) {
    New-Item -ItemType Directory -Path $PackageOutput | Out-Null
}
dotnet pack $ProjectPath --configuration Release --no-build --output $PackageOutput
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Package creation failed" -ForegroundColor Red
    exit 1
}
Write-Host "Package created" -ForegroundColor Green
Write-Host ""

# Step 5: Resolve actual package version and file
$actualVersion = $Version
$versionMatch = Select-String -Path $ProjectPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if ($versionMatch -and $versionMatch.Matches.Groups.Count -gt 1) {
    $actualVersion = $versionMatch.Matches.Groups[1].Value.Trim()
}

$PackageFile = Join-Path $PackageOutput "$PackageName.$actualVersion.nupkg"
if (-not (Test-Path $PackageFile)) {
    Write-Host "Error: Package file not found: $PackageFile" -ForegroundColor Red
    $existingPackages = Get-ChildItem -Path $PackageOutput -Filter "*.nupkg" -ErrorAction SilentlyContinue
    if ($existingPackages) {
        Write-Host "Found package files:" -ForegroundColor Yellow
        $existingPackages | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Yellow }
    }
    exit 1
}

Write-Host "Step 5: Publishing package to GitHub Packages..." -ForegroundColor Yellow

function Test-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName
    )
    try {
        $headers = @{
            "Authorization" = "Bearer $Token"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $versionsUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions"
        $versionsResponse = Invoke-RestMethod -Uri $versionsUrl -Method Get -Headers $headers -ErrorAction Stop
        $existingVersion = $versionsResponse | Where-Object { $_.name -eq $PackageVersion } | Select-Object -First 1
        if ($existingVersion) {
            return @{ Exists = $true; VersionId = $existingVersion.id }
        } else {
            return @{ Exists = $false; VersionId = $null }
        }
    } catch {
        return @{ Exists = $null; VersionId = $null }
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
            "Authorization" = "Bearer $Token"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $deleteUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions/$VersionId"
        Invoke-RestMethod -Uri $deleteUrl -Method Delete -Headers $headers -ErrorAction Stop
        Write-Host "  Package version deleted successfully" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "  Could not delete package via API: $_" -ForegroundColor Yellow
        return $false
    }
}

$versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace

if ($versionCheck.Exists -eq $true) {
    Write-Host "  Package version $actualVersion already exists, deleting to override..." -ForegroundColor Yellow
    $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace -VersionId $versionCheck.VersionId
    if (-not $deleted) {
        Write-Host "Error: Could not delete existing package version" -ForegroundColor Red
        Write-Host "  1. Go to https://github.com/$Namespace/$RepoName/packages" -ForegroundColor Yellow
        Write-Host "  2. Select Caching.NET, delete version $actualVersion, then run this script again" -ForegroundColor Yellow
        exit 1
    }
    Start-Sleep -Seconds 2
    Write-Host "  Publishing new version..." -ForegroundColor Yellow
} elseif ($versionCheck.Exists -eq $false) {
    Write-Host "  Package version $actualVersion does not exist, creating new version..." -ForegroundColor Green
} else {
    Write-Host "  Publishing package (will handle errors if version exists)..." -ForegroundColor Yellow
}

dotnet nuget push $PackageFile `
    --api-key $GitHubPAT `
    --source $SourceName

if ($LASTEXITCODE -ne 0) {
    $pushOutput = dotnet nuget push $PackageFile --api-key $GitHubPAT --source $SourceName 2>&1 | Out-String
    $pushOutputLower = $pushOutput.ToLower()
    $packageExists = $pushOutputLower -match "already exists" -or $pushOutputLower -match "conflict" -or $pushOutputLower -match "409" -or $pushOutputLower -match "package.*exist"

    if ($packageExists) {
        Write-Host "  Package version still exists, attempting to delete and republish..." -ForegroundColor Yellow
        $versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace
        if ($versionCheck.Exists -eq $true) {
            $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace -VersionId $versionCheck.VersionId
            if ($deleted) {
                Start-Sleep -Seconds 2
                dotnet nuget push $PackageFile --api-key $GitHubPAT --source $SourceName
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Error: Package publish failed after deletion" -ForegroundColor Red
                    exit 1
                }
            } else {
                Write-Host "Error: Could not delete existing package version" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "Error: Package publish failed" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Error: Package publish failed" -ForegroundColor Red
        Write-Host "Error details: $pushOutput" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Package published successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "View your package at: https://github.com/$Namespace/$RepoName/packages"
Write-Host ""

