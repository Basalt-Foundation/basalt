# Basalt Testnet Deployment Guide

Deploy a 4-validator Basalt testnet using Cloudflare Tunnel. No open ports needed — Cloudflare handles HTTPS, DDoS protection, and global routing.

## Architecture

```
Users (HTTPS)
    |
    v
+------------------+
|  Cloudflare CDN  |  DDoS protection, HTTPS, global edge
+--------+---------+
         | (tunnel)
         v
+---------------------------------------------+
| Windows/Linux VPS (no ports exposed)        |
|                                              |
| +------------+                               |
| | cloudflared |  Cloudflare Tunnel agent     |
| +------+-----+                               |
|        |                                     |
| +------+-----+                               |
| |   Caddy    |  Host-based routing + CORS   |
| +------+-----+                               |
|        |                                     |
|   basalt.foundation    testnet.basalt.foundation
|        |                       |             |
| +------+------+  +------+------+------+------+
| |   Website   |  |Val-0 |Val-1 |Val-2 |Val-3 |
| | (Next.js)   |  |(RPC) |      |      |      |
| +-------------+  +------+------+------+------+
|                                              |
| Chain ID: 4242 | Block time: 2s             |
| Consensus: BasaltBFT (3f+1 = 4 nodes)      |
+---------------------------------------------+
```

## Step 1: Create a Cloudflare Tunnel

1. Go to [Cloudflare Zero Trust](https://one.dash.cloudflare.com/)
2. Navigate to **Networks > Tunnels**
3. Click **Create a tunnel** > select **Cloudflared**
4. Name it `basalt-testnet`
5. Copy the **tunnel token** (you'll need it in Step 3)
6. In the **Public Hostnames** tab, add two routes:
   - **Route 1 (Testnet):**
     - **Subdomain**: `testnet`
     - **Domain**: `basalt.foundation`
     - **Service**: `http://caddy:80`
   - **Route 2 (Website):**
     - **Subdomain**: *(leave empty for root domain)*
     - **Domain**: `basalt.foundation`
     - **Service**: `http://caddy:80`
7. Save

Your testnet will be accessible at `https://testnet.basalt.foundation` and the website at `https://basalt.foundation`.

## Step 2: Install Prerequisites (Windows VPS)

### Docker Desktop

1. Download from [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop/)
2. Install with **WSL2 backend** enabled
3. After install, ensure **Linux containers** mode is active (right-click tray icon)
4. In Settings > Resources, set **Memory** to 4+ GB and **Disk** to 20+ GB

### Git

Download from [git-scm.com](https://git-scm.com/download/win) if not already installed.

## Step 3: Deploy

Open **PowerShell** and run:

```powershell
# Clone the repo
git clone https://github.com/basalt-foundation/basalt.git $env:USERPROFILE\basalt
cd $env:USERPROFILE\basalt\deploy\testnet

# Run setup (will prompt for your Cloudflare Tunnel token)
.\setup.ps1
```

The script will:
1. Verify Docker is running in Linux container mode
2. Generate 4 validator key pairs
3. Prompt for your Cloudflare Tunnel token
4. Build all Docker images (validators, explorer, website)
5. Start the 4-validator testnet, website, and cloudflared tunnel
6. Clean up build cache to save disk space

## Step 4: Verify

```powershell
# Check all 7 containers are running
docker compose ps

# Check blockchain status (via tunnel)
Invoke-RestMethod https://testnet.yourdomain.com/v1/status

# Or locally
Invoke-RestMethod http://localhost:80/v1/status
```

You should see block height increasing every 2s.

## Operations

### View logs

```powershell
docker compose logs -f                # All services
docker compose logs -f validator-0    # Single validator
docker compose logs -f cloudflared    # Tunnel status
```

### Restart / Stop / Reset

```powershell
docker compose restart       # Restart all
docker compose down          # Stop all
docker compose down -v       # Stop + wipe all chain data
```

### Update to latest

```powershell
cd $env:USERPROFILE\basalt
git pull
cd deploy\testnet
docker compose build --no-cache
docker compose up -d
docker builder prune -f      # Reclaim disk space
```

### Storage management

With limited disk space, periodically clean up:

```powershell
docker system prune -f       # Remove unused images/containers
docker system df             # Check disk usage
```

Expected usage: **~8-10 GB** (images + chain data).

## Linux Server Deployment

Same steps, using `setup.sh` instead:

```bash
git clone https://github.com/basalt-foundation/basalt.git ~/basalt
cd ~/basalt/deploy/testnet
chmod +x setup.sh
./setup.sh
```

## Public Endpoints

Once deployed, the following endpoints are available at your tunnel domain:

| Endpoint | Description |
|----------|-------------|
| `GET /v1/status` | Node status, block height, peer count |
| `GET /v1/blocks/latest` | Latest finalized block |
| `GET /v1/blocks/:height` | Block by height |
| `POST /v1/transactions` | Submit a transaction |
| `GET /v1/transactions/:hash` | Transaction by hash |
| `GET /v1/accounts/:address` | Account state |
| `POST /v1/faucet` | Get test BSLT tokens |
| `GET /graphql` | GraphQL playground |
| `/ws/blocks` | WebSocket block subscription |
