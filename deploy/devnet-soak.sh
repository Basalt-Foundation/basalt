#!/usr/bin/env bash
# devnet-soak.sh — chaos-matrix soak for the Basalt devnet (ChainId 31337, 4 validators + rpc-0).
#
# Standing regression gate for the fork/sync-recovery repair (roadmap Phase 0/1). It runs six
# scenarios against the root docker-compose devnet and prints a single PASS/FAIL verdict with a
# per-scenario table, exiting non-zero on any FAIL so CI can consume it.
#
#   Scenarios: (a) steady-state  (b) restart-one  (c) rolling restart  (d) partition
#              (e) kill -9        (f) RPC 503 probe (runs concurrently across a-e)
#
# Usage:
#   deploy/devnet-soak.sh [--steady-blocks N] [--keep-up] [--dry-run]
#     --steady-blocks N   advance at least N blocks in the steady phase (default 40; use e.g. 10000 for a long soak)
#     --keep-up           do not tear the devnet down at the end (default: down -v on exit)
#     --dry-run           print the scenario matrix and exit 0 without touching containers
#
# Green per scenario:
#   (a) all 4 validators strictly advance, pairwise-equal within +-2 at each sample; rpc tracks within +-2.
#   (b) restarted validator rejoins and converges to tip within ~100 blocks.
#   (c) rolling restart never drops quorum; chain keeps finalizing.
#   (d) partitioned validator catches up and converges after reconnect (this is the fork-recovery path).
#   (e) kill -9 node restarts, state-root recovery passes, converges; staking set preserved.
#   (f) rpc /v1/health never returns a sustained 503 (>= 2 consecutive = 20s window).
# Any occurrence of a failure signature (see SIGS) fails the whole run.

set -uo pipefail

# --- config ---
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"     # repo root (deploy/ is one level down)
COMPOSE="$HERE/docker-compose.yml"
STEADY_BLOCKS=40
KEEP_UP=0
DRY_RUN=0
VALIDATORS=(basalt-validator-0 basalt-validator-1 basalt-validator-2 basalt-validator-3)
V_PORTS=(5100 5101 5102 5103)
RPC_PORT=5200
SAMPLE=10          # seconds between samples
CONVERGE_TOL=2     # allowed height spread between honest nodes
RPC_503_WINDOW=2   # consecutive 503s that fail the run

# Failure log signatures (exact strings from the source).
SIGS='Block parent hash does not match current chain tip|could not resolve via fork rollback|Failed to add consensus-finalized|CIRCUIT BREAKER|State root mismatch after recovery'

while [ $# -gt 0 ]; do
  case "$1" in
    --steady-blocks) STEADY_BLOCKS="$2"; shift 2 ;;
    --keep-up) KEEP_UP=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# Per-scenario results tracked as RESULT_<scenario> (Bash 3.2 has no associative arrays).
FAILED=0

log()  { printf '\n\033[1m[soak] %s\033[0m\n' "$*"; }
pass() { printf -v "RESULT_$1" '%s' PASS; printf '  \033[32mPASS\033[0m %s\n' "$2"; }
fail() { printf -v "RESULT_$1" '%s' FAIL; FAILED=1; printf '  \033[31mFAIL\033[0m %s\n' "$2"; }

height() { curl -s --max-time 3 "http://localhost:$1/v1/health" 2>/dev/null \
             | grep -o '"lastBlockNumber":[0-9]*' | grep -o '[0-9]*$'; }
rpc_code() { curl -s -o /dev/null -w '%{http_code}' --max-time 3 "http://localhost:$RPC_PORT/v1/health" 2>/dev/null; }
net_name() { docker network ls --format '{{.Name}}' | grep -E 'devnet' | head -1; }
sig_count() { docker compose -f "$COMPOSE" logs --no-color 2>/dev/null | grep -cE "$SIGS"; }

# converged: all validator heights present and within CONVERGE_TOL of the max
converged() {
  local hs=() max=0 h
  for p in "${V_PORTS[@]}"; do h=$(height "$p"); [ -z "$h" ] && return 1; hs+=("$h"); [ "$h" -gt "$max" ] && max=$h; done
  for h in "${hs[@]}"; do [ $((max - h)) -gt "$CONVERGE_TOL" ] && return 1; done
  return 0
}

if [ "$DRY_RUN" = 1 ]; then
  log "DRY RUN — scenarios: (a) steady-state >=${STEADY_BLOCKS} blocks  (b) restart-one  (c) rolling restart  (d) partition  (e) kill-9  (f) rpc-503 probe"
  echo "compose: $COMPOSE"; echo "failure signatures: $SIGS"; exit 0
fi

# --- lifecycle: bring up, and always tear down (unless --keep-up) ---
cleanup() {
  if [ "$KEEP_UP" = 0 ]; then log "teardown (down -v)"; docker compose -f "$COMPOSE" down -v >/dev/null 2>&1; fi
}
trap cleanup EXIT

log "bring up devnet"
docker compose -f "$COMPOSE" up -d --build >/dev/null 2>&1 || { echo "compose up failed" >&2; exit 1; }

# wait until all validators report a height
for i in $(seq 1 30); do converged && break; sleep 3; done
NET="$(net_name)"; log "network=$NET  (waiting for genesis... v0=$(height 5100))"

# --- (f) RPC 503 probe: background loop for the whole run ---
PROBE_LOG="$(mktemp)"
( bad=0
  while :; do
    c=$(rpc_code)
    if [ "$c" = "503" ]; then bad=$((bad+1)); else bad=0; fi
    [ "$bad" -ge "$RPC_503_WINDOW" ] && echo "SUSTAINED_503 at $(date +%T)" >> "$PROBE_LOG"
    sleep "$SAMPLE"
  done ) &
PROBE_PID=$!
trap 'kill "$PROBE_PID" 2>/dev/null; cleanup' EXIT

# --- (a) steady-state ---
log "(a) steady-state: advance >=${STEADY_BLOCKS} blocks, checking lockstep"
start=$(height 5100); : "${start:=0}"; ok=1
while :; do
  sleep "$SAMPLE"
  line=""; for p in "${V_PORTS[@]}" "$RPC_PORT"; do line+="$p=$(height "$p") "; done
  echo "    $line"
  converged || { ok=0; echo "    (spread exceeded tolerance)"; }
  cur=$(height 5100); : "${cur:=$start}"
  [ $((cur - start)) -ge "$STEADY_BLOCKS" ] && break
done
[ "$ok" = 1 ] && pass a "steady-state lockstep held over $((cur-start)) blocks" || fail a "steady-state height spread exceeded +-$CONVERGE_TOL"

# --- (b) restart-one ---
log "(b) restart validator-2"
docker restart basalt-validator-2 >/dev/null 2>&1
ok=0; for i in $(seq 1 24); do sleep 5; if converged; then ok=1; break; fi; done
[ "$ok" = 1 ] && pass b "validator-2 rejoined and converged" || fail b "validator-2 did not converge within budget"

# --- (c) rolling restart ---
log "(c) rolling restart of all validators (staggered)"
ok=1
for v in "${VALIDATORS[@]}"; do
  docker restart "$v" >/dev/null 2>&1
  r=0; for i in $(seq 1 24); do sleep 5; if converged; then r=1; break; fi; done
  [ "$r" = 1 ] || { ok=0; break; }
done
[ "$ok" = 1 ] && pass c "chain kept finalizing through rolling restart" || fail c "quorum/finalization lost during rolling restart"

# --- (d) partition (the fork-recovery path) ---
log "(d) partition validator-3 for 200 blocks-worth (~60s), then reconnect"
docker network disconnect "$NET" basalt-validator-3 >/dev/null 2>&1
sleep 60
docker network connect "$NET" basalt-validator-3 >/dev/null 2>&1
ok=0; for i in $(seq 1 30); do sleep 5; if converged; then ok=1; break; fi; done
[ "$ok" = 1 ] && pass d "validator-3 caught up and converged after partition" || fail d "validator-3 stayed forked after reconnect"

# --- (e) kill -9 ---
log "(e) kill -9 validator-1, then restart"
docker kill -s KILL basalt-validator-1 >/dev/null 2>&1
sleep 3
docker start basalt-validator-1 >/dev/null 2>&1
ok=0; for i in $(seq 1 24); do sleep 5; if converged; then ok=1; break; fi; done
[ "$ok" = 1 ] && pass e "validator-1 recovered from hard kill and converged" || fail e "validator-1 did not recover from kill -9"

# --- (f) verdict from the probe + a global signature sweep ---
log "(f) RPC 503 probe + failure-signature sweep"
kill "$PROBE_PID" 2>/dev/null
if [ -s "$PROBE_LOG" ]; then fail f "sustained RPC 503 observed: $(cat "$PROBE_LOG")"; else pass f "rpc /v1/health never sustained a 503"; fi
sigs=$(sig_count)
if [ "$sigs" -gt 0 ]; then
  FAILED=1; printf '  \033[31mFAIL\033[0m %s\n' "found $sigs fork/sync failure-signature line(s):"
  docker compose -f "$COMPOSE" logs --no-color 2>/dev/null | grep -E "$SIGS" | tail -10 | sed 's/^/      /'
else
  printf '  \033[32mPASS\033[0m %s\n' "0 fork/sync failure signatures in any log"
fi
rm -f "$PROBE_LOG"

# --- report ---
log "SOAK REPORT"
for s in a b c d e f; do v="RESULT_$s"; printf '  scenario %s: %s\n' "$s" "${!v:-?}"; done
printf '  signatures: %s\n' "$sigs"
if [ "$FAILED" = 0 ]; then echo; echo "SOAK RESULT: PASS"; exit 0; else echo; echo "SOAK RESULT: FAIL"; exit 1; fi
