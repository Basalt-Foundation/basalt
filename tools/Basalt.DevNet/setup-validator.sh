#!/bin/bash
# Basalt Validator Setup Script
# This script configures a validator node for the Basalt testnet.

set -e

echo "=== Basalt Validator Setup ==="
echo ""

# Configuration
BASALT_HOME="${BASALT_HOME:-$HOME/.basalt}"
BASALT_DATA="${BASALT_HOME}/data"
BASALT_CONFIG="${BASALT_HOME}/config"
BASALT_LOGS="${BASALT_HOME}/logs"

# Create directories
echo "Creating directories..."
mkdir -p "$BASALT_DATA"
mkdir -p "$BASALT_CONFIG"
mkdir -p "$BASALT_LOGS"

# Check if .NET runtime is available
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET runtime not found. Install .NET 9.0 SDK from https://dot.net"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
echo "  .NET version: $DOTNET_VERSION"

# Generate validator key pair
echo ""
echo "Generating validator key pair..."
if [ -f "$BASALT_CONFIG/validator.key" ]; then
    echo "  Key pair already exists at $BASALT_CONFIG/validator.key"
    echo "  To regenerate, delete the file and run this script again."
else
    # Use the Basalt CLI if available, otherwise use a placeholder
    if command -v basalt &> /dev/null; then
        basalt account create --output "$BASALT_CONFIG/validator.key"
        chmod 600 "$BASALT_CONFIG/validator.key"
    else
        echo "  Basalt CLI not installed. Install with: dotnet tool install -g Basalt.Cli"
        echo "  Then run: basalt account create --output $BASALT_CONFIG/validator.key"
    fi
fi

# Write default config
echo ""
echo "Writing configuration..."
cat > "$BASALT_CONFIG/basalt.json" << 'EOF'
{
  "network": "basalt-testnet",
  "chainId": 31337,
  "node": {
    "dataDir": "~/.basalt/data",
    "logDir": "~/.basalt/logs",
    "apiPort": 5000,
    "p2pPort": 30303,
    "metricsEnabled": true
  },
  "validator": {
    "enabled": true,
    "keyFile": "~/.basalt/config/validator.key"
  },
  "consensus": {
    "blockTimeMs": 400,
    "maxTransactionsPerBlock": 10000,
    "blockGasLimit": 100000000
  },
  "peers": {
    "bootstrapNodes": [
      "boot1.basalt.network:30303",
      "boot2.basalt.network:30303",
      "boot3.basalt.network:30303"
    ],
    "maxPeers": 50
  },
  "logging": {
    "level": "Information",
    "console": true,
    "file": true
  }
}
EOF

echo "  Configuration written to $BASALT_CONFIG/basalt.json"

# Summary
echo ""
echo "=== Setup Complete ==="
echo ""
echo "Directories:"
echo "  Data:   $BASALT_DATA"
echo "  Config: $BASALT_CONFIG"
echo "  Logs:   $BASALT_LOGS"
echo ""
echo "Next steps:"
echo "  1. Edit $BASALT_CONFIG/basalt.json to configure your node"
echo "  2. Start the node: basalt-node --config $BASALT_CONFIG/basalt.json"
echo "  3. Monitor: curl http://localhost:5000/v1/status"
echo "  4. View metrics: curl http://localhost:5000/metrics"
echo ""
echo "For Docker deployment:"
echo "  docker compose -f docker-compose.yml up -d"
echo ""
