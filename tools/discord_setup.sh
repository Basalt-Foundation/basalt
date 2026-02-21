#!/bin/bash
# Basalt Network Discord Server Setup Script
set -e

TOKEN=$(cat ~/.discord_basalt_token | tr -d '\n')
GUILD="1474773805116162059"
API="https://discord.com/api/v10"
AUTH="Authorization: Bot $TOKEN"

# Helper: Discord API call with rate-limit handling
api() {
  local METHOD="$1" ENDPOINT="$2" DATA="$3"
  local RESPONSE
  while true; do
    if [ -n "$DATA" ]; then
      RESPONSE=$(curl -s -w "\n%{http_code}" -X "$METHOD" -H "$AUTH" -H "Content-Type: application/json" "$API$ENDPOINT" -d "$DATA")
    else
      RESPONSE=$(curl -s -w "\n%{http_code}" -X "$METHOD" -H "$AUTH" -H "Content-Type: application/json" "$API$ENDPOINT")
    fi
    local HTTP_CODE=$(echo "$RESPONSE" | tail -1)
    local BODY=$(echo "$RESPONSE" | sed '$d')
    if [ "$HTTP_CODE" = "429" ]; then
      local RETRY=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('retry_after',1))" 2>/dev/null || echo "1")
      echo "  Rate limited, waiting ${RETRY}s..." >&2
      sleep "$RETRY"
      continue
    fi
    echo "$BODY"
    return 0
  done
}

# Extract ID from JSON response
get_id() {
  echo "$1" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])"
}

echo "=== Basalt Network Discord Setup ==="
echo ""

# ─────────────────────────────────────────
# STEP 1: Create Roles
# ─────────────────────────────────────────
echo "--- Creating Roles ---"

# Colors (decimal):
#   Basalt Orange: 0xE67E22 = 15105570
#   Red (Admin):   0xE74C3C = 15158332
#   Blue (Mod):    0x3498DB = 3447003
#   Green (Val):   0x2ECC71 = 3066993
#   Purple (Dev):  0x9B59B6 = 10181046
#   Gold (Core):   0xF1C40F = 15844367
#   Teal (Contrib):0x1ABC9C = 1752220
#   Grey (Comm):   0x95A5A6 = 9807270

echo "  Creating Admin role..."
R_ADMIN=$(api POST "/guilds/$GUILD/roles" '{"name":"Admin","color":15158332,"hoist":true,"permissions":"8","mentionable":false}')
ADMIN_ID=$(get_id "$R_ADMIN")
echo "    Admin: $ADMIN_ID"

echo "  Creating Core Team role..."
R_CORE=$(api POST "/guilds/$GUILD/roles" '{"name":"Core Team","color":15844367,"hoist":true,"permissions":"1071698660929","mentionable":false}')
CORE_ID=$(get_id "$R_CORE")
echo "    Core Team: $CORE_ID"

echo "  Creating Moderator role..."
R_MOD=$(api POST "/guilds/$GUILD/roles" '{"name":"Moderator","color":3447003,"hoist":true,"permissions":"1099511627799","mentionable":false}')
MOD_ID=$(get_id "$R_MOD")
echo "    Moderator: $MOD_ID"

echo "  Creating Validator role..."
R_VAL=$(api POST "/guilds/$GUILD/roles" '{"name":"Validator","color":3066993,"hoist":true,"permissions":"1024","mentionable":true}')
VAL_ID=$(get_id "$R_VAL")
echo "    Validator: $VAL_ID"

echo "  Creating Developer role..."
R_DEV=$(api POST "/guilds/$GUILD/roles" '{"name":"Developer","color":10181046,"hoist":true,"permissions":"1024","mentionable":true}')
DEV_ID=$(get_id "$R_DEV")
echo "    Developer: $DEV_ID"

echo "  Creating Contributor role..."
R_CONTRIB=$(api POST "/guilds/$GUILD/roles" '{"name":"Contributor","color":1752220,"hoist":false,"permissions":"1024","mentionable":true}')
CONTRIB_ID=$(get_id "$R_CONTRIB")
echo "    Contributor: $CONTRIB_ID"

echo "  Creating Community role..."
R_COMM=$(api POST "/guilds/$GUILD/roles" '{"name":"Community","color":9807270,"hoist":false,"permissions":"1024","mentionable":false}')
COMM_ID=$(get_id "$R_COMM")
echo "    Community: $COMM_ID"

EVERYONE_ID="$GUILD"

echo ""
echo "--- Deleting Default Channels ---"

# Delete the 4 default channels
for CH_ID in 1474773806076788919 1474773806076788920 1474773806076788917 1474773806076788918; do
  echo "  Deleting $CH_ID..."
  api DELETE "/channels/$CH_ID" > /dev/null
  sleep 0.3
done

echo ""
echo "--- Creating Categories & Channels ---"

# Helper: create a read-only channel for @everyone (announcements-style)
# Staff (Admin, Core, Mod) can send messages
readonly_overwrites() {
  cat <<EOJSON
[
  {"id":"$EVERYONE_ID","type":0,"deny":"2048","allow":"0"},
  {"id":"$ADMIN_ID","type":0,"deny":"0","allow":"2048"},
  {"id":"$CORE_ID","type":0,"deny":"0","allow":"2048"},
  {"id":"$MOD_ID","type":0,"deny":"0","allow":"2048"}
]
EOJSON
}

# Helper: staff-only channel (hidden from everyone)
staff_overwrites() {
  cat <<EOJSON
[
  {"id":"$EVERYONE_ID","type":0,"deny":"1024","allow":"0"},
  {"id":"$ADMIN_ID","type":0,"deny":"0","allow":"1024"},
  {"id":"$CORE_ID","type":0,"deny":"0","allow":"1024"},
  {"id":"$MOD_ID","type":0,"deny":"0","allow":"1024"}
]
EOJSON
}

# Helper: validator-only channel
validator_overwrites() {
  cat <<EOJSON
[
  {"id":"$EVERYONE_ID","type":0,"deny":"1024","allow":"0"},
  {"id":"$VAL_ID","type":0,"deny":"0","allow":"3072"},
  {"id":"$ADMIN_ID","type":0,"deny":"0","allow":"3072"},
  {"id":"$CORE_ID","type":0,"deny":"0","allow":"3072"},
  {"id":"$MOD_ID","type":0,"deny":"0","allow":"3072"}
]
EOJSON
}

# ── CATEGORY: ANNOUNCEMENTS ──
echo "  Creating category: ANNOUNCEMENTS"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Announcements","type":4,"position":0}')
CAT_ANN=$(get_id "$CAT")

OW=$(readonly_overwrites)
echo "  Creating #announcements"
api POST "/guilds/$GUILD/channels" "{\"name\":\"announcements\",\"type\":0,\"parent_id\":\"$CAT_ANN\",\"topic\":\"Official Basalt Network announcements and updates\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #roadmap"
api POST "/guilds/$GUILD/channels" "{\"name\":\"roadmap\",\"type\":0,\"parent_id\":\"$CAT_ANN\",\"topic\":\"Development roadmap and milestone tracking\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #releases"
api POST "/guilds/$GUILD/channels" "{\"name\":\"releases\",\"type\":0,\"parent_id\":\"$CAT_ANN\",\"topic\":\"Software releases, changelogs, and upgrade notices\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

# ── CATEGORY: INFORMATION ──
echo "  Creating category: INFORMATION"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Information","type":4,"position":1}')
CAT_INFO=$(get_id "$CAT")

echo "  Creating #welcome"
OW=$(readonly_overwrites)
api POST "/guilds/$GUILD/channels" "{\"name\":\"welcome\",\"type\":0,\"parent_id\":\"$CAT_INFO\",\"topic\":\"Welcome to Basalt Network — start here\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #rules"
api POST "/guilds/$GUILD/channels" "{\"name\":\"rules\",\"type\":0,\"parent_id\":\"$CAT_INFO\",\"topic\":\"Server rules and community guidelines\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #links"
api POST "/guilds/$GUILD/channels" "{\"name\":\"links\",\"type\":0,\"parent_id\":\"$CAT_INFO\",\"topic\":\"GitHub, documentation, explorer, and official links\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #faq"
api POST "/guilds/$GUILD/channels" "{\"name\":\"faq\",\"type\":0,\"parent_id\":\"$CAT_INFO\",\"topic\":\"Frequently asked questions about Basalt\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

# ── CATEGORY: COMMUNITY ──
echo "  Creating category: COMMUNITY"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Community","type":4,"position":2}')
CAT_COMM=$(get_id "$CAT")

echo "  Creating #general"
api POST "/guilds/$GUILD/channels" "{\"name\":\"general\",\"type\":0,\"parent_id\":\"$CAT_COMM\",\"topic\":\"General discussion about Basalt Network\"}" > /dev/null
sleep 0.3

echo "  Creating #introductions"
api POST "/guilds/$GUILD/channels" "{\"name\":\"introductions\",\"type\":0,\"parent_id\":\"$CAT_COMM\",\"topic\":\"Introduce yourself to the community\"}" > /dev/null
sleep 0.3

echo "  Creating #ideas-feedback"
api POST "/guilds/$GUILD/channels" "{\"name\":\"ideas-feedback\",\"type\":0,\"parent_id\":\"$CAT_COMM\",\"topic\":\"Share ideas and feedback for improving Basalt\"}" > /dev/null
sleep 0.3

echo "  Creating #memes"
api POST "/guilds/$GUILD/channels" "{\"name\":\"memes\",\"type\":0,\"parent_id\":\"$CAT_COMM\",\"topic\":\"Blockchain memes and fun content\"}" > /dev/null
sleep 0.3

echo "  Creating #off-topic"
api POST "/guilds/$GUILD/channels" "{\"name\":\"off-topic\",\"type\":0,\"parent_id\":\"$CAT_COMM\",\"topic\":\"Everything not related to Basalt\"}" > /dev/null
sleep 0.3

# ── CATEGORY: DEVELOPMENT ──
echo "  Creating category: DEVELOPMENT"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Development","type":4,"position":3}')
CAT_DEV=$(get_id "$CAT")

echo "  Creating #dev-general"
api POST "/guilds/$GUILD/channels" "{\"name\":\"dev-general\",\"type\":0,\"parent_id\":\"$CAT_DEV\",\"topic\":\"General development discussion\"}" > /dev/null
sleep 0.3

echo "  Creating #sdk-help"
api POST "/guilds/$GUILD/channels" "{\"name\":\"sdk-help\",\"type\":0,\"parent_id\":\"$CAT_DEV\",\"topic\":\"Help with Basalt SDK, contract development, and tooling\"}" > /dev/null
sleep 0.3

echo "  Creating #smart-contracts"
api POST "/guilds/$GUILD/channels" "{\"name\":\"smart-contracts\",\"type\":0,\"parent_id\":\"$CAT_DEV\",\"topic\":\"Smart contract development, deployment, and best practices\"}" > /dev/null
sleep 0.3

echo "  Creating #bug-reports"
api POST "/guilds/$GUILD/channels" "{\"name\":\"bug-reports\",\"type\":0,\"parent_id\":\"$CAT_DEV\",\"topic\":\"Report bugs and issues — check GitHub Issues first\"}" > /dev/null
sleep 0.3

echo "  Creating #github-feed"
OW=$(readonly_overwrites)
api POST "/guilds/$GUILD/channels" "{\"name\":\"github-feed\",\"type\":0,\"parent_id\":\"$CAT_DEV\",\"topic\":\"Automated GitHub activity feed (commits, PRs, issues)\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

# ── CATEGORY: TECHNICAL ──
echo "  Creating category: TECHNICAL"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Technical","type":4,"position":4}')
CAT_TECH=$(get_id "$CAT")

echo "  Creating #node-operators"
api POST "/guilds/$GUILD/channels" "{\"name\":\"node-operators\",\"type\":0,\"parent_id\":\"$CAT_TECH\",\"topic\":\"Running Basalt nodes — setup, configuration, troubleshooting\"}" > /dev/null
sleep 0.3

echo "  Creating #validators"
OW=$(validator_overwrites)
api POST "/guilds/$GUILD/channels" "{\"name\":\"validators\",\"type\":0,\"parent_id\":\"$CAT_TECH\",\"topic\":\"Validator-only channel — operations, coordination, alerts\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

echo "  Creating #testnet"
api POST "/guilds/$GUILD/channels" "{\"name\":\"testnet\",\"type\":0,\"parent_id\":\"$CAT_TECH\",\"topic\":\"Testnet/devnet coordination, faucet requests, and testing\"}" > /dev/null
sleep 0.3

echo "  Creating #network-status"
OW=$(readonly_overwrites)
api POST "/guilds/$GUILD/channels" "{\"name\":\"network-status\",\"type\":0,\"parent_id\":\"$CAT_TECH\",\"topic\":\"Automated network status and chain metrics\",\"permission_overwrites\":$OW}" > /dev/null
sleep 0.3

# ── CATEGORY: GOVERNANCE ──
echo "  Creating category: GOVERNANCE"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Governance","type":4,"position":5}')
CAT_GOV=$(get_id "$CAT")

echo "  Creating #proposals"
api POST "/guilds/$GUILD/channels" "{\"name\":\"proposals\",\"type\":0,\"parent_id\":\"$CAT_GOV\",\"topic\":\"On-chain governance proposals and voting\"}" > /dev/null
sleep 0.3

echo "  Creating #governance-discussion"
api POST "/guilds/$GUILD/channels" "{\"name\":\"governance-discussion\",\"type\":0,\"parent_id\":\"$CAT_GOV\",\"topic\":\"Discuss active and upcoming governance proposals\"}" > /dev/null
sleep 0.3

# ── CATEGORY: VOICE ──
echo "  Creating category: VOICE"
CAT=$(api POST "/guilds/$GUILD/channels" '{"name":"Voice","type":4,"position":6}')
CAT_VOICE=$(get_id "$CAT")

echo "  Creating General voice channel"
api POST "/guilds/$GUILD/channels" "{\"name\":\"General\",\"type\":2,\"parent_id\":\"$CAT_VOICE\"}" > /dev/null
sleep 0.3

echo "  Creating Dev Talk voice channel"
api POST "/guilds/$GUILD/channels" "{\"name\":\"Dev Talk\",\"type\":2,\"parent_id\":\"$CAT_VOICE\"}" > /dev/null
sleep 0.3

echo "  Creating Community Call voice channel"
api POST "/guilds/$GUILD/channels" "{\"name\":\"Community Call\",\"type\":2,\"parent_id\":\"$CAT_VOICE\"}" > /dev/null
sleep 0.3

# ── CATEGORY: STAFF (Hidden) ──
echo "  Creating category: STAFF (hidden)"
OW=$(staff_overwrites)
CAT=$(api POST "/guilds/$GUILD/channels" "{\"name\":\"Staff\",\"type\":4,\"position\":7,\"permission_overwrites\":$OW}")
CAT_STAFF=$(get_id "$CAT")

echo "  Creating #staff-chat"
api POST "/guilds/$GUILD/channels" "{\"name\":\"staff-chat\",\"type\":0,\"parent_id\":\"$CAT_STAFF\",\"topic\":\"Internal team discussion\"}" > /dev/null
sleep 0.3

echo "  Creating #mod-log"
api POST "/guilds/$GUILD/channels" "{\"name\":\"mod-log\",\"type\":0,\"parent_id\":\"$CAT_STAFF\",\"topic\":\"Moderation actions and audit log\"}" > /dev/null
sleep 0.3

echo "  Creating #bot-config"
api POST "/guilds/$GUILD/channels" "{\"name\":\"bot-config\",\"type\":0,\"parent_id\":\"$CAT_STAFF\",\"topic\":\"Bot configuration and testing\"}" > /dev/null
sleep 0.3

echo ""
echo "=== Setup Complete ==="
echo ""
echo "Categories: 8"
echo "Text channels: 22"
echo "Voice channels: 3"
echo "Roles: 7 (+ @everyone + bot)"
