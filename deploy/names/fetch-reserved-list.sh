#!/usr/bin/env bash
# Builds the reserved-label list from Cloudflare Radar's domain rankings.
#
# The list decides which names are held back at launch, so how it was produced has to be reproducible by
# someone who does not trust us. This script is that record: same source, same filter, same date, same
# list. Run it, commit the output, and the ranking date in the header is what anyone re-runs against.
#
# Needs a Cloudflare API token with the "Radar Read" permission:
#   https://dash.cloudflare.com/profile/api-tokens -> Create Token -> Custom -> Account / Radar / Read
#
# Usage:
#   CF_RADAR_TOKEN=... ./fetch-reserved-list.sh [count] [output]

set -euo pipefail

COUNT="${1:-500}"
OUT="${2:-$(dirname "$0")/reserved-com.txt}"

if [ -z "${CF_RADAR_TOKEN:-}" ]; then
  echo "CF_RADAR_TOKEN is not set. Create a token with Radar Read at:" >&2
  echo "  https://dash.cloudflare.com/profile/api-tokens" >&2
  exit 1
fi

for tool in curl jq; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done

# Ask for more than we need, because the ranking spans every TLD and only .com is being reserved.
FETCH=$((COUNT * 8))
if [ "$FETCH" -gt 5000 ]; then FETCH=5000; fi

echo "fetching top $FETCH domains from Cloudflare Radar" >&2

RESPONSE="$(curl -sS --fail-with-body \
  -H "Authorization: Bearer $CF_RADAR_TOKEN" \
  "https://api.cloudflare.com/client/v4/radar/ranking/top?limit=${FETCH}&format=json")"

if [ "$(echo "$RESPONSE" | jq -r '.success')" != "true" ]; then
  echo "Radar API refused the request:" >&2
  echo "$RESPONSE" | jq -r '.errors // .' >&2
  exit 1
fi

RANKED_ON="$(echo "$RESPONSE" | jq -r '.result.meta.dateRange[0].startTime // "unknown"')"

# Second-level .com only. A ranking entry like "mail.google.com" is a host, not a registrable name, and
# reserving it would hold back something nobody can claim through a domain registrar.
LABELS="$(echo "$RESPONSE" \
  | jq -r '.result.top_0[].domain' \
  | grep -E '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.com$' \
  | sed 's/\.com$//' \
  | awk '!seen[$0]++' \
  | head -n "$COUNT")"

FOUND="$(echo "$LABELS" | grep -c . || true)"
if [ "$FOUND" -lt "$COUNT" ]; then
  # Said out loud rather than written silently. A short list is a set of names left unprotected, and it
  # should be a decision someone makes, not something they discover later.
  echo "WARNING: only $FOUND second-level .com domains in the top $FETCH, wanted $COUNT" >&2
fi

{
  echo "# Reserved .bslt labels, held for the owners of the matching .com domains."
  echo "#"
  echo "# Source:  Cloudflare Radar domain rankings, top $FETCH"
  echo "# Ranked:  $RANKED_ON"
  echo "# Filter:  second-level .com only, deduplicated, first $COUNT"
  echo "# Count:   $FOUND"
  echo "#"
  echo "# Regenerate with deploy/names/fetch-reserved-list.sh. Anyone can re-run it against the same"
  echo "# ranking date and get the same list, which is the point of writing it down this way."
  echo "$LABELS"
} > "$OUT"

echo "wrote $FOUND labels to $OUT" >&2
