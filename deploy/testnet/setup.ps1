# ─── Basalt Testnet Setup Script (Windows + Cloudflare Tunnel) ────────
# Run this in PowerShell on a Windows Server with Docker Desktop (WSL2).
#
# Prerequisites:
#   1. Docker Desktop installed with WSL2 backend, Linux containers mode
#   2. Git installed
#   3. A Cloudflare Tunnel token (from Zero Trust dashboard)
#
# Usage:
#   .\setup.ps1
# ─────────────────────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"

# Detect if running from inside the repo already
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path "$scriptDir\..\..").Path

if (Test-Path "$repoRoot\Basalt.sln") {
    $BASALT_DIR = $repoRoot
} else {
    $BASALT_DIR = "$env:USERPROFILE\basalt"
}
$DEPLOY_DIR = "$BASALT_DIR\deploy\testnet"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Basalt Testnet Setup (Windows)"            -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ─── Step 1: Check Docker ────────────────────────────────────────────
Write-Host "[1/6] Checking Docker..." -ForegroundColor Yellow
try {
    docker version | Out-Null
    docker compose version | Out-Null
    Write-Host "  Docker is installed and running."
} catch {
    Write-Host "  ERROR: Docker is not installed or not running." -ForegroundColor Red
    Write-Host "  Install Docker Desktop: https://docs.docker.com/desktop/install/windows-install/" -ForegroundColor Red
    Write-Host "  Make sure WSL2 backend is enabled and 'Use Linux containers' is selected." -ForegroundColor Red
    exit 1
}

# Check that Docker is in Linux container mode
$dockerInfo = docker info 2>&1 | Out-String
if ($dockerInfo -match "OSType:\s+windows") {
    Write-Host "  ERROR: Docker is running in Windows container mode." -ForegroundColor Red
    Write-Host "  Right-click Docker Desktop tray icon > 'Switch to Linux containers'" -ForegroundColor Red
    exit 1
}
Write-Host "  Docker is running Linux containers."

# ─── Step 2: Verify repo ──────────────────────────────────────────────
if (Test-Path "$BASALT_DIR\Basalt.sln") {
    Write-Host "[2/6] Basalt repo found at $BASALT_DIR" -ForegroundColor Yellow
} else {
    Write-Host "[2/6] ERROR: Basalt repo not found." -ForegroundColor Red
    Write-Host "  Clone it first: git clone https://github.com/basalt-foundation/basalt.git" -ForegroundColor Red
    exit 1
}

Set-Location $DEPLOY_DIR

# ─── Step 3: Generate validator keys and configure ────────────────────
if (Test-Path ".env") {
    Write-Host "[3/6] .env already exists, skipping key generation." -ForegroundColor Yellow
    Write-Host "  Delete .env and re-run to regenerate keys."
} else {
    Write-Host "[3/6] Generating validator keys..." -ForegroundColor Yellow

    function New-ValidatorKey {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        return ($bytes | ForEach-Object { $_.ToString("x2") }) -join ""
    }

    function New-ValidatorAddress {
        param([int]$Index)
        return "0x" + ("0" * 36) + "01" + $Index.ToString("00")
    }

    # Prompt for Cloudflare Tunnel token
    Write-Host ""
    Write-Host "  You need a Cloudflare Tunnel token." -ForegroundColor Cyan
    Write-Host "  Get one from: https://one.dash.cloudflare.com/ > Networks > Tunnels > Create" -ForegroundColor Cyan
    Write-Host "  Configure the tunnel to route your domain to http://caddy:80" -ForegroundColor Cyan
    Write-Host ""
    $tunnelToken = Read-Host "  Enter your Cloudflare Tunnel token"

    if ([string]::IsNullOrWhiteSpace($tunnelToken)) {
        Write-Host "  WARNING: No tunnel token provided. Set CLOUDFLARE_TUNNEL_TOKEN in .env later." -ForegroundColor Yellow
        $tunnelToken = "PASTE_YOUR_TOKEN_HERE"
    }

    $timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    $envContent = @"
# Basalt Testnet Configuration
# Generated on $timestamp
# WARNING: These are testnet keys. Never use these for mainnet.

# Cloudflare Tunnel
CLOUDFLARE_TUNNEL_TOKEN=$tunnelToken

# Validator 0
VALIDATOR_0_KEY=$(New-ValidatorKey)
VALIDATOR_0_ADDRESS=$(New-ValidatorAddress 0)

# Validator 1
VALIDATOR_1_KEY=$(New-ValidatorKey)
VALIDATOR_1_ADDRESS=$(New-ValidatorAddress 1)

# Validator 2
VALIDATOR_2_KEY=$(New-ValidatorKey)
VALIDATOR_2_ADDRESS=$(New-ValidatorAddress 2)

# Validator 3
VALIDATOR_3_KEY=$(New-ValidatorKey)
VALIDATOR_3_ADDRESS=$(New-ValidatorAddress 3)
"@

    [System.IO.File]::WriteAllText("$DEPLOY_DIR\.env", $envContent)
    Write-Host "  Keys generated and saved to .env"
}

# ─── Step 4: Build containers ────────────────────────────────────────
Write-Host "[4/6] Building Docker images (this may take several minutes)..." -ForegroundColor Yellow
docker compose build --no-cache

# ─── Step 5: Start testnet ───────────────────────────────────────────
Write-Host "[5/6] Starting testnet..." -ForegroundColor Yellow
docker compose up -d

# ─── Step 6: Clean up build cache ────────────────────────────────────
Write-Host "[6/6] Cleaning up build cache to save disk space..." -ForegroundColor Yellow
docker builder prune -f 2>$null

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  Basalt Testnet is starting!"               -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Validators:  4 nodes (BFT consensus)"
Write-Host "  Chain ID:    4242"
Write-Host "  Block time:  2s"
Write-Host ""
Write-Host "  The testnet is accessible via your Cloudflare Tunnel domain."
Write-Host "  No firewall ports need to be opened." -ForegroundColor Green
Write-Host ""
Write-Host "  Check status:"
Write-Host "    docker compose logs -f"
Write-Host ""
Write-Host "  Stop testnet:"
Write-Host "    docker compose down"
Write-Host ""
