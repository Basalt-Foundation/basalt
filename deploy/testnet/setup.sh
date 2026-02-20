#!/usr/bin/env bash
set -euo pipefail

# ─── Basalt Testnet Setup Script (Linux + Cloudflare Tunnel) ─────────
# Run this on a fresh Ubuntu 22.04/24.04 server.
#
# Usage:
#   chmod +x setup.sh && ./setup.sh
# ─────────────────────────────────────────────────────────────────────

BASALT_DIR="$HOME/basalt"
DEPLOY_DIR="$BASALT_DIR/deploy/testnet"

echo "============================================"
echo "  Basalt Testnet Setup"
echo "============================================"
echo ""

# ─── Step 1: Install Docker ──────────────────────────────────────────
if ! command -v docker &>/dev/null; then
    echo "[1/6] Installing Docker..."
    curl -fsSL https://get.docker.com | sh
    sudo usermod -aG docker "$USER"
    echo "  Docker installed. You may need to log out and back in for group changes."
else
    echo "[1/6] Docker already installed."
fi

if ! docker compose version &>/dev/null; then
    echo "ERROR: docker compose plugin not found."
    exit 1
fi

# ─── Step 2: Clone Basalt ────────────────────────────────────────────
if [ -d "$BASALT_DIR" ]; then
    echo "[2/6] Basalt repo exists, pulling latest..."
    cd "$BASALT_DIR" && git pull
else
    echo "[2/6] Cloning Basalt..."
    git clone https://github.com/basalt-foundation/basalt.git "$BASALT_DIR"
fi

cd "$DEPLOY_DIR"

# ─── Step 3: Generate validator keys and configure ────────────────────
if [ -f .env ]; then
    echo "[3/6] .env already exists, skipping key generation."
else
    echo "[3/6] Generating validator keys..."

    generate_key() {
        openssl rand -hex 32
    }

    generate_address() {
        local idx=$1
        printf "0x%038d%02d" 1 "$idx"
    }

    echo ""
    echo "  You need a Cloudflare Tunnel token."
    echo "  Get one from: https://one.dash.cloudflare.com/ > Networks > Tunnels > Create"
    echo "  Configure the tunnel to route your domain to http://caddy:80"
    echo ""
    read -rp "  Enter your Cloudflare Tunnel token: " tunnel_token

    if [ -z "$tunnel_token" ]; then
        echo "  WARNING: No tunnel token provided. Set CLOUDFLARE_TUNNEL_TOKEN in .env later."
        tunnel_token="PASTE_YOUR_TOKEN_HERE"
    fi

    cat > .env <<EOF
# Basalt Testnet Configuration
# Generated on $(date -u +"%Y-%m-%dT%H:%M:%SZ")
# WARNING: These are testnet keys. Never use these for mainnet.

# Cloudflare Tunnel
CLOUDFLARE_TUNNEL_TOKEN=$tunnel_token

# Validator 0
VALIDATOR_0_KEY=$(generate_key)
VALIDATOR_0_ADDRESS=$(generate_address 0)

# Validator 1
VALIDATOR_1_KEY=$(generate_key)
VALIDATOR_1_ADDRESS=$(generate_address 1)

# Validator 2
VALIDATOR_2_KEY=$(generate_key)
VALIDATOR_2_ADDRESS=$(generate_address 2)

# Validator 3
VALIDATOR_3_KEY=$(generate_key)
VALIDATOR_3_ADDRESS=$(generate_address 3)
EOF

    echo "  Keys generated and saved to .env"
fi

# ─── Step 4: Build containers ────────────────────────────────────────
echo "[4/6] Building Docker images (this may take a few minutes)..."
docker compose build --no-cache

# ─── Step 5: Start testnet ───────────────────────────────────────────
echo "[5/6] Starting testnet..."
docker compose up -d

# ─── Step 6: Clean up build cache ────────────────────────────────────
echo "[6/6] Cleaning up build cache to save disk space..."
docker builder prune -f 2>/dev/null || true

echo ""
echo "============================================"
echo "  Basalt Testnet is starting!"
echo "============================================"
echo ""
echo "  Validators:  4 nodes (BFT consensus)"
echo "  Chain ID:    4242"
echo "  Block time:  2s"
echo ""
echo "  The testnet is accessible via your Cloudflare Tunnel domain."
echo "  No firewall ports need to be opened."
echo ""
echo "  Check status:"
echo "    docker compose logs -f"
echo ""
echo "  Stop testnet:"
echo "    docker compose down"
echo ""
