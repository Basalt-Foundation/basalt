#!/bin/bash
# Post welcome messages to Basalt Network Discord
set -e

TOKEN=$(cat ~/.discord_basalt_token | tr -d '\n')
API="https://discord.com/api/v10"
AUTH="Authorization: Bot $TOKEN"

api_post() {
  local ENDPOINT="$1" DATA="$2"
  while true; do
    RESPONSE=$(curl -s -w "\n%{http_code}" -X POST -H "$AUTH" -H "Content-Type: application/json" "$API$ENDPOINT" -d "$DATA")
    HTTP_CODE=$(echo "$RESPONSE" | tail -1)
    BODY=$(echo "$RESPONSE" | sed '$d')
    if [ "$HTTP_CODE" = "429" ]; then
      RETRY=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('retry_after',2))" 2>/dev/null || echo "2")
      echo "  Rate limited, waiting ${RETRY}s..." >&2
      sleep "$RETRY"
      continue
    fi
    echo "$BODY"
    return 0
  done
}

# Channel IDs
CH_WELCOME="1474775908140191854"
CH_RULES="1474775911302828245"
CH_LINKS="1474775914289299661"
CH_FAQ="1474775918651248733"
CH_ANNOUNCEMENTS="1474775898174783508"

echo "--- Posting Welcome Message ---"
read -r -d '' WELCOME_MSG << 'MSGEOF' || true
{
  "embeds": [{
    "title": "Welcome to Basalt Network",
    "description": "**Basalt** is a next-generation Layer 1 blockchain built on .NET 9 with Native AOT compilation.\n\nWe combine the performance and safety of modern .NET with cutting-edge cryptography and a developer-first approach to smart contracts.\n\n**What makes Basalt unique:**\n\n> **Native AOT Performance** — No JIT warmup, instant startup, minimal memory footprint\n> **BFT Consensus** — Pipelined Byzantine Fault Tolerant consensus with dynamic validator sets\n> **SDK Contracts** — Write smart contracts in C# with full IDE support and source-generated dispatch\n> **ZK Compliance** — Privacy-preserving regulatory compliance with Groth16 proofs\n> **EIP-1559 Fees** — Dynamic base fee with priority tips for predictable gas pricing\n> **EVM Bridge** — Native bridge to Ethereum with M-of-N multisig security\n\nGet started by checking out the channels below and introducing yourself in <#1474775925668446466>!",
    "color": 15105570,
    "footer": {"text": "Basalt Network — Built Different"}
  }]
}
MSGEOF
api_post "/channels/$CH_WELCOME/messages" "$WELCOME_MSG" > /dev/null
echo "  Welcome message posted."
sleep 0.5

echo "--- Posting Rules ---"
read -r -d '' RULES_MSG << 'MSGEOF' || true
{
  "embeds": [{
    "title": "Server Rules",
    "description": "By participating in this server, you agree to the following rules:\n\n**1. Be Respectful**\nTreat all members with respect. No harassment, hate speech, discrimination, or personal attacks.\n\n**2. No Spam or Self-Promotion**\nDo not spam messages, links, or unsolicited promotions. Share projects in relevant channels only.\n\n**3. Stay On Topic**\nUse channels for their intended purpose. Off-topic conversations belong in <#1474775934648193088>.\n\n**4. No Financial Advice**\nDo not provide or solicit financial, investment, or trading advice. This is a technology community.\n\n**5. No Scams or Phishing**\nNever share or click suspicious links. Basalt team will **never** DM you first asking for funds or private keys.\n\n**6. English Preferred**\nPlease use English in public channels to keep discussions accessible to everyone.\n\n**7. Respect Privacy**\nDo not share others' personal information. What's shared in private stays private.\n\n**8. Follow Discord ToS**\nAll Discord Terms of Service and Community Guidelines apply.\n\n*Moderators reserve the right to take action on any behavior that disrupts the community.*",
    "color": 3447003,
    "footer": {"text": "Violations may result in warnings, mutes, or bans"}
  }]
}
MSGEOF
api_post "/channels/$CH_RULES/messages" "$RULES_MSG" > /dev/null
echo "  Rules posted."
sleep 0.5

echo "--- Posting Links ---"
read -r -d '' LINKS_MSG << 'MSGEOF' || true
{
  "embeds": [{
    "title": "Official Links",
    "description": "**Source Code & Development**\n[GitHub Repository](https://github.com/reyancarlier/Basalt) — Main codebase, issues, and pull requests\n\n**Documentation**\nDesign Plan and Technical Spec available in the `docs/` directory of the repository\n\n**Network**\nBlock Explorer and API endpoints will be shared here as they become publicly available\n\n**Social**\nFollow us on X (Twitter) for the latest updates\n\n---\n*Only trust links posted in this channel or by team members. Never click links from DMs claiming to be Basalt staff.*",
    "color": 15105570,
    "footer": {"text": "Last updated: February 2026"}
  }]
}
MSGEOF
api_post "/channels/$CH_LINKS/messages" "$LINKS_MSG" > /dev/null
echo "  Links posted."
sleep 0.5

echo "--- Posting FAQ ---"
read -r -d '' FAQ_MSG << 'MSGEOF' || true
{
  "embeds": [{
    "title": "Frequently Asked Questions",
    "description": "**What is Basalt?**\nBasalt is a Layer 1 blockchain built from scratch in .NET 9 with Native AOT compilation, designed for high performance and developer ergonomics.\n\n**What consensus mechanism does Basalt use?**\nBasalt uses a pipelined BFT (Byzantine Fault Tolerant) consensus with dynamic validator sets managed through epoch-based staking.\n\n**How do I write smart contracts?**\nContracts are written in C# using the Basalt SDK. The source generator handles dispatch, storage, and serialization — no reflection needed.\n\n**What cryptography does Basalt use?**\n- **BLAKE3** for hashing\n- **Ed25519** for transaction signatures\n- **BLS12-381** for aggregate signatures\n- **Keccak-256** for address derivation\n- **Groth16** for zero-knowledge proofs\n\n**Is there a testnet?**\nA devnet with 4 validators runs via Docker Compose. Public testnet details will be announced in <#1474775898174783508>.\n\n**How do I become a validator?**\nValidator registration requires a minimum stake of 100,000 BST. Details on the staking process will be shared as the network matures.\n\n**How can I contribute?**\nCheck the GitHub repository for open issues, or ask in <#1474775939195076609> about areas where help is needed.",
    "color": 15844367,
    "footer": {"text": "Have more questions? Ask in #general"}
  }]
}
MSGEOF
api_post "/channels/$CH_FAQ/messages" "$FAQ_MSG" > /dev/null
echo "  FAQ posted."
sleep 0.5

echo "--- Posting First Announcement ---"
read -r -d '' ANN_MSG << 'MSGEOF' || true
{
  "embeds": [{
    "title": "Welcome to the Basalt Network Community!",
    "description": "We're excited to officially open the Basalt Network Discord server.\n\nThis is the central hub for our community — whether you're a developer interested in building on Basalt, a validator looking to secure the network, or just curious about what we're building.\n\n**Current Status:**\n- Core protocol implementation complete (Phases 1-6)\n- 2,100+ tests passing across 16 test projects\n- SDK with 7 token standards and 8 system contracts\n- EIP-1559 dynamic fees, ZK compliance, EVM bridge\n- 4-validator devnet running in Docker\n\nStay tuned for more updates. We're building something different.\n\n*— The Basalt Team*",
    "color": 15105570
  }]
}
MSGEOF
api_post "/channels/$CH_ANNOUNCEMENTS/messages" "$ANN_MSG" > /dev/null
echo "  First announcement posted."

echo ""
echo "=== All messages posted ==="
