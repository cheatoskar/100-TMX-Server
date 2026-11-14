#!/usr/bin/env bash
# Turns on the shared leaderboard + open-maps list for the TMNF servers (2026-11-14):
# TMNF on the game box is the hub, TMNF 2 on the second machine its client. Run from this PC after
# the build with SyncHub is deployed:
#   bash deploy/enable-sync.sh
# The token is made once and kept outside the repo (../sync-token.txt); it only travels on stdin.
# Only config.toml changes (with a backup next to it); the restart is deploy-when-empty's job.
set -euo pipefail

D=$(cd "$(dirname "$0")" && pwd)
GAME=ubuntu@141.144.230.233
SECOND=ubuntu@152.70.183.80
TOKEN_FILE="$D/../../sync-token.txt"

if [ ! -s "$TOKEN_FILE" ]; then
  python -c "import secrets; print(secrets.token_urlsafe(32))" > "$TOKEN_FILE"
  chmod 600 "$TOKEN_FILE"
fi
TOKEN=$(tr -d '\r\n' < "$TOKEN_FILE")

apply() { # host, config path, json without the path
  local host=$1 path=$2 json=$3
  scp -q "$D/set-config-keys.py" "$host:/tmp/set-config-keys.py"
  printf '%s' "${json/__PATH__/$path}" | ssh "$host" 'sudo python3 /tmp/set-config-keys.py; rm -f /tmp/set-config-keys.py'
}

COMMON='"EventEnabled": "true", "EventEveryday": "true", "EventMapsSource": "\"tmx\"", "EventAfterPreset": "\"\""'
TABLES='"tables": {"EventTmxQuery": "inhasrecord = 0\n"}'

echo "== TMNF (hub, game box)"
apply "$GAME" /opt/randomizer-anywhere/publish/config.toml \
  "{\"path\": \"__PATH__\", \"keys\": {$COMMON, \"EventServerLabel\": \"\\\"TMNF\\\"\", \"EventUnlimiterMaps\": \"\\\"skip\\\"\", \"EventStateFile\": \"\\\"/opt/randomizer-anywhere/publish/open-maps-state.json\\\"\", \"SyncAllowedIps\": \"\\\"152.70.183.80\\\"\"}, $TABLES, \"secret_keys\": {\"SyncToken\": \"$TOKEN\"}}"

echo "== TMNF 2 (client, second machine)"
apply "$SECOND" /opt/tmx-tmnf2/publish/config.toml \
  "{\"path\": \"__PATH__\", \"keys\": {$COMMON, \"EventServerLabel\": \"\\\"TMNF 2\\\"\", \"EventUnlimiterMaps\": \"\\\"include\\\"\", \"EventStateFile\": \"\\\"/opt/tmx-tmnf2/publish/open-maps-state.json\\\"\", \"SyncHubUrl\": \"\\\"http://141.144.230.233:8090\\\"\"}, $TABLES, \"secret_keys\": {\"SyncToken\": \"$TOKEN\"}}"

echo "== done - restart both (hub first) with deploy-when-empty to take effect"
