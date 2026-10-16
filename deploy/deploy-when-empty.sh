#!/bin/bash
# Deploys a new build to the live server(s) WITHOUT ever kicking anybody.
#
#     DEPLOY_HOST=<user>@<server> bash deploy-when-empty.sh <folder with the published build>
#
# The build folder is the output of: dotnet publish -c Release -o <folder>
#
# <user> can be root, or any user with passwordless sudo (e.g. `ubuntu` on an Oracle/cloud image,
# where root login is disabled) - the server-side steps then run through `sudo -n`.
#
# For every server instance that exists on the machine (TMNF, and TMUF once it is set up) it:
#   - uploads RandomizerAnywhere.dll/.pdb to /tmp first (nothing is changed yet)
#   - waits until THAT instance has had no players for 2 minutes in a row
#   - re-checks the player count right before touching anything
#   - backs up the current files, swaps them in, restarts that instance's service
#   - checks that it came back healthy, and puts the old files back if it did not
# An instance whose service is not running (e.g. TMUF before its login is filled in) just gets the
# new files, with no restart. Instances are independent: the TMNF server never waits for TMUF.
#
# Start it in the background and leave the computer on until it says it is done:
#     nohup bash deploy-when-empty.sh <folder> > deploy-when-empty.log 2>&1 &
#     tail -f deploy-when-empty.log

HOST="${DEPLOY_HOST:-}"           # e.g. ubuntu@1.2.3.4 - deliberately not in the repo
POLL_SECONDS=30
EMPTY_POLLS_NEEDED=4              # 4 x 30s = empty for 2 minutes in a row
GIVE_UP_AFTER_SECONDS=$((24*3600))

P="${1:-}"
if [ -z "$P" ] || [ ! -f "$P/RandomizerAnywhere.dll" ]; then
  echo "usage: bash deploy-when-empty.sh <folder containing the published RandomizerAnywhere.dll>"
  exit 1
fi
if [ -z "$HOST" ]; then
  echo "set DEPLOY_HOST first, e.g.:  DEPLOY_HOST=ubuntu@1.2.3.4 bash deploy-when-empty.sh <folder>"
  exit 1
fi

# name | folder on the server | systemd service | status page port
INSTANCES=(
  "tmnf|/opt/randomizer-anywhere/publish|100tmx|8090"
  "tmuf|/opt/randomizer-anywhere-tmuf/publish|100tmx-tmuf|8091"
)

SSH="ssh -o BatchMode=yes -o ConnectTimeout=10 $HOST"
log() { echo "[$(date '+%F %T')] $*"; }

# Swapping files in /opt and restarting services needs root. Logged in as root that is a given;
# as anybody else, check up front that passwordless sudo works - finding out after a 2-hour wait
# for an empty server, mid-deploy, would be the worst moment.
REMOTE_SUDO=""
if [ "${HOST%%@*}" != root ]; then
  REMOTE_SUDO="sudo -n"
  $SSH "sudo -n true" 2>/dev/null || { log "$HOST has no passwordless sudo - aborting, server untouched."; exit 1; }
fi

# which instances exist on the server?
pending=()
declare -A DIR SVC PORT STREAK
for entry in "${INSTANCES[@]}"; do
  IFS='|' read -r name dir svc port <<< "$entry"
  if $SSH "test -f $dir/RandomizerAnywhere.dll"; then
    pending+=("$name"); DIR[$name]=$dir; SVC[$name]=$svc; PORT[$name]=$port; STREAK[$name]=0
    log "found instance: $name ($dir)"
  else
    log "no $name instance on the server - skipping it"
  fi
done
[ ${#pending[@]} -gt 0 ] || { log "nothing to deploy to."; exit 1; }

log "Uploading the new build to the server (nothing is changed yet)..."
scp -o BatchMode=yes "$P/RandomizerAnywhere.dll" "$P/RandomizerAnywhere.pdb" $HOST:/tmp/ || { log "Upload failed - aborting, server untouched."; exit 1; }
$SSH 'ls -l /tmp/RandomizerAnywhere.dll /tmp/RandomizerAnywhere.pdb' || exit 1

# prints the instance's player count; "0" if its service is not running at all, "?" if it is
# running but cannot be asked
players() {
  local name=$1
  $SSH "if [ \"\$(systemctl is-active ${SVC[$name]})\" != active ]; then echo 0; exit 0; fi
        c=\$(curl -s --max-time 5 http://127.0.0.1:${PORT[$name]}/status.json | grep -o '\"PlayerCount\":[0-9]*' | cut -d: -f2)
        echo \${c:-?}" 2>/dev/null | grep -E '^([0-9]+|\?)$' || echo "?"
}

deploy_one() {
  local name=$1
  local backup="/opt/randomizer-anywhere/backup-$(date +%Y%m%d-%H%M%S)-$name"
  $SSH $REMOTE_SUDO bash -s -- "${DIR[$name]}" "${SVC[$name]}" "${PORT[$name]}" "$backup" <<'REMOTE'
set -u
dir=$1; svc=$2; port=$3; backup=$4
cd "$dir" || { echo FAILED; exit 0; }
was_active=0
[ "$(systemctl is-active "$svc")" = active ] && was_active=1
if [ $was_active = 1 ]; then
  # last-second re-check: if somebody joined during the wait, change nothing at all
  curl -s --max-time 5 "http://127.0.0.1:$port/status.json" | grep -q '"PlayerCount":0' || { echo BUSY; exit 0; }
fi
mkdir -p "$backup"
cp RandomizerAnywhere.dll RandomizerAnywhere.pdb "$backup"/
install -m 644 /tmp/RandomizerAnywhere.dll /tmp/RandomizerAnywhere.pdb .
if [ $was_active = 0 ]; then echo "DEPLOYED_NOT_RUNNING $backup"; exit 0; fi
# The game watchdog would see the status page go quiet during the restart and could restart the
# service a second time mid-boot; this starts its 5-minute cooldown.
mkdir -p /run/tmx-game-watchdog && date +%s > /run/tmx-game-watchdog/restarted
t0=$(date +%s)
systemctl restart "$svc"
# Healthy = a status.json written AFTER the restart by a controller that reports itself healthy.
# Just finding "PlayerCount" is not enough: status.json is a file on disk, so the old process's
# last copy is served for the first seconds and made a broken build look fine.
ok=0
for i in $(seq 1 30); do
  sleep 3
  s=$(curl -s --max-time 5 "http://127.0.0.1:$port/status.json" || true)
  upd=$(printf '%s' "$s" | grep -o '"UpdatedAt":"[^"]*"' | cut -d'"' -f4)
  ts=$(date -d "$upd" +%s 2>/dev/null || echo 0)
  if [ "$(systemctl is-active "$svc")" = active ] && [ "$ts" -gt "$t0" ] \
     && printf '%s' "$s" | grep -q '"ControllerHealthy":true'; then ok=1; break; fi
done
if [ $ok = 1 ]; then echo "DEPLOYED $backup"; else
  cp "$backup"/RandomizerAnywhere.dll "$backup"/RandomizerAnywhere.pdb .
  systemctl restart "$svc"
  echo "ROLLED_BACK $backup"
fi
REMOTE
}

started=$(date +%s)
while [ ${#pending[@]} -gt 0 ]; do
  now=$(date +%s)
  if [ $((now - started)) -ge $GIVE_UP_AFTER_SECONDS ]; then
    log "Gave up after 24h. Not deployed to: ${pending[*]}. Those servers are untouched."
    exit 2
  fi

  still=()
  for name in "${pending[@]}"; do
    count=$(players "$name")
    if [ "$count" = "0" ]; then
      STREAK[$name]=$(( ${STREAK[$name]} + 1 ))
      log "$name: empty (${STREAK[$name]}/$EMPTY_POLLS_NEEDED)"
    else
      [ "$count" = "?" ] && log "$name: could not read the player count - treating as busy" || log "$name: $count player(s) online - waiting"
      STREAK[$name]=0
    fi

    if [ ${STREAK[$name]} -ge $EMPTY_POLLS_NEEDED ]; then
      log "$name: empty long enough - deploying."
      result=$(deploy_one "$name")
      log "$name: server said: $result"
      case "$result" in
        DEPLOYED_NOT_RUNNING*) log "$name: new files installed (service is not running, so no restart)." ;;
        DEPLOYED*)             log "$name: DEPLOYED. Backup of the old build is on the server (${result#DEPLOYED })." ;;
        ROLLED_BACK*)          log "$name: ROLLED BACK - the new build did not come up healthy, the old one was restored." ;;
        BUSY*)                 log "$name: a player joined at the last second - waiting for the next empty window."; STREAK[$name]=0; still+=("$name") ;;
        *)                     log "$name: unexpected answer - not retrying automatically." ;;
      esac
    else
      still+=("$name")
    fi
  done
  pending=("${still[@]}")
  [ ${#pending[@]} -gt 0 ] && sleep $POLL_SECONDS
done

log "All done."
