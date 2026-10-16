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
#   - uploads the WHOLE build (program files, presets, web assets - not just the .dll) to a
#     staging folder first; nothing live is changed yet
#   - waits until THAT instance has had no players for 2 minutes in a row
#   - re-checks the player count right before touching anything
#   - backs up the files it is about to replace, swaps the new ones in, restarts the service
#   - checks that it came back healthy, and puts the old files back if it did not (and checks
#     that the old build is healthy again)
# The server-side part runs as its own systemd unit, so a laptop going to sleep or a dropped SSH
# connection can't cut a deploy off half-way - this script just waits for its result.
# Runtime data on the server (config.toml, leaderboard, skip/impossible/played lists, status,
# the dedicated server itself) is never shipped or overwritten.
#
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
RESULT_WAIT_SECONDS=900           # how long to wait for the server-side part to report back

P="${1:-}"
if [ -z "$P" ] || [ ! -f "$P/RandomizerAnywhere.dll" ]; then
  echo "usage: bash deploy-when-empty.sh <folder containing the published RandomizerAnywhere.dll>"
  exit 1
fi
if [ -z "$HOST" ]; then
  echo "set DEPLOY_HOST first, e.g.:  DEPLOY_HOST=ubuntu@1.2.3.4 bash deploy-when-empty.sh <folder>"
  exit 1
fi

# These live on the server and belong to it. Never part of a build upload.
EXCLUDES=(
  --exclude=./config.toml
  --exclude='./leaderboard.json*'
  --exclude=./skipped-maps.json
  --exclude=./impossible-maps.json
  --exclude=./played-maps.json
  --exclude='./*.corrupt-*'
  --exclude=./WebStatus/status.json
  --exclude=./Servers
  --exclude=./CANNOT_SEE_SERVER.txt
)

# name | folder on the server | systemd service | status page port
INSTANCES=(
  "tmnf|/opt/randomizer-anywhere/publish|100tmx|8090"
  "tmuf|/opt/randomizer-anywhere-tmuf/publish|100tmx-tmuf|8091"
)

SSH="ssh -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 $HOST"
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

# One staging folder per deploy, under /opt (not /tmp: a reboot during the wait empties /tmp, and
# the old script then "deployed" nothing and reported success).
STAGE="/opt/randomizer-anywhere/staging-$(date +%Y%m%d-%H%M%S)-$$"
log "Uploading the whole build to $STAGE (nothing live is changed yet)..."
$SSH "$REMOTE_SUDO mkdir -p $STAGE/build && $REMOTE_SUDO chown \$(id -u):\$(id -g) $STAGE $STAGE/build" \
  || { log "Could not create the staging folder - aborting, server untouched."; exit 1; }
# pipefail: a local tar that fails half-way must count as a failed upload, not just a failed ssh
( set -o pipefail; tar -C "$P" "${EXCLUDES[@]}" -czf - . | $SSH "tar -xzf - -C $STAGE/build" ) \
  || { log "Upload failed - aborting, server untouched."; exit 1; }
$SSH "test -f $STAGE/build/RandomizerAnywhere.dll && test -f $STAGE/build/RandomizerAnywhere.deps.json" \
  || { log "Upload incomplete - aborting, server untouched."; exit 1; }
log "Uploaded $($SSH "find $STAGE/build -type f | wc -l") files."

# The server-side part. Uploaded as a file and run by systemd, independent of this SSH session.
# It writes exactly one word (plus details) to <stage>/result-<name> when it is done.
cat <<'REMOTE' | $SSH "cat > $STAGE/deploy-remote.sh" || { log "Could not upload the server-side script - aborting."; exit 1; }
#!/bin/bash
set -uo pipefail
build=$1; dir=$2; svc=$3; port=$4; backup=$5; result=$6

finish() { echo "$*" > "$result.tmp" && mv "$result.tmp" "$result"; exit 0; }

# one deploy per instance at a time - a second one would back up half-new files as "the old build"
exec 9>"/run/lock/tmx-deploy-$svc.lock"
flock -n 9 || finish "FAILED another deploy of $svc is running right now"

# A status.json is only evidence if it is fresh: a hung old build keeps serving its last
# "PlayerCount":0 forever.
status_age() {
  local upd ts
  upd=$(printf '%s' "$1" | grep -o '"UpdatedAt":"[^"]*"' | cut -d'"' -f4)
  ts=$(date -d "$upd" +%s 2>/dev/null || echo 0)
  echo $(( $(date +%s) - ts ))
}

# Healthy = a status.json written AFTER t0 by a controller that reports itself healthy, twice in a
# row. status.json is a file on disk, so the old process's last copy is served for the first
# seconds; just finding "PlayerCount" made a broken build look fine.
healthy_since() {
  local t0=$1 good=0 s upd ts
  for i in $(seq 1 60); do
    sleep 3
    s=$(curl -s --max-time 5 "http://127.0.0.1:$port/status.json" || true)
    upd=$(printf '%s' "$s" | grep -o '"UpdatedAt":"[^"]*"' | cut -d'"' -f4)
    ts=$(date -d "$upd" +%s 2>/dev/null || echo 0)
    if [ "$(systemctl is-active "$svc")" = active ] && [ "$ts" -gt "$t0" ] \
       && printf '%s' "$s" | grep -q '"ControllerHealthy":true'; then
      good=$((good + 1))
      [ $good -ge 2 ] && return 0
    else
      good=0
    fi
  done
  return 1
}

cd "$dir" || finish "FAILED cannot enter $dir"
[ -f "$build/RandomizerAnywhere.dll" ] || finish "FAILED the uploaded build is gone ($build)"

# Second line of defence behind the upload's excludes: whatever belongs to the server is never
# taken from a build, even if a future build or exclude list gets it wrong.
for protected in config.toml leaderboard.json leaderboard.json.bak leaderboard.json.tmp skipped-maps.json \
                 impossible-maps.json played-maps.json CANNOT_SEE_SERVER.txt WebStatus/status.json WebStatus/status.json.tmp; do
  rm -f "$build/$protected"
done
rm -rf "$build/Servers"
find "$build" -name '*.corrupt-*' -delete

# "active" -> running, needs the empty check and a restart; "inactive"/"failed" -> not running,
# just swap the files. Anything else (activating, auto-restart, deactivating) is in motion:
# don't touch it now.
state=$(systemctl is-active "$svc")
case "$state" in
  active)          was_active=1 ;;
  inactive|failed) was_active=0 ;;
  *)               finish "BUSY service is $state" ;;
esac

if [ $was_active = 1 ]; then
  # last-second re-check: if somebody joined during the wait, change nothing at all
  s=$(curl -s --max-time 5 "http://127.0.0.1:$port/status.json" || true)
  printf '%s' "$s" | grep -q '"PlayerCount":0,' || finish "BUSY"
  printf '%s' "$s" | grep -q '"PlayerCountStale":true' && finish "BUSY player count unknown"
  [ "$(status_age "$s")" -le 45 ] || finish "BUSY status.json is stale"
fi

# back up exactly the files that are about to be replaced
mkdir -p "$backup" || finish "FAILED cannot create the backup folder"
( cd "$build" && find . -type f -print0 ) | while IFS= read -r -d '' f; do
  if [ -f "$dir/$f" ]; then
    mkdir -p "$backup/$(dirname "$f")" && cp -p "$dir/$f" "$backup/$f" || exit 1
  fi
done || finish "FAILED backing up the current build (disk full?) - nothing was changed"

# --remove-destination: replace each file instead of rewriting it in place - the running process
# has the old DLLs mapped, and overwriting a mapped file under it can crash it mid-copy.
# --preserve without ownership: uploaded as the SSH user, but in /opt the files belong to root.
restore() {
  cp -R --remove-destination --preserve=mode,timestamps "$backup/." "$dir/"
}

cp -R --remove-destination --preserve=mode,timestamps "$build/." "$dir/" \
  || { restore; finish "FAILED copying the new build - the old files were put back"; }

if [ $was_active = 0 ]; then finish "DEPLOYED_NOT_RUNNING ($state) $backup"; fi

# The game watchdog would see the status page go quiet during the restart and could restart the
# service a second time mid-boot; this starts its 5-minute cooldown.
mkdir -p /run/tmx-game-watchdog && date +%s > /run/tmx-game-watchdog/restarted
systemctl restart "$svc"
# t0 after the restart has returned: the old process may still write status.json while stopping
t0=$(date +%s)
if healthy_since "$t0"; then
  finish "DEPLOYED $backup"
fi

restore
date +%s > /run/tmx-game-watchdog/restarted
systemctl restart "$svc"
t1=$(date +%s)
if healthy_since "$t1"; then
  finish "ROLLED_BACK $backup"
fi
finish "ROLLED_BACK_BUT_UNHEALTHY $backup"
REMOTE

# prints the instance's player count; "0" if its service is not running at all, "?" if it is
# running but cannot be asked, only knows a stale count, or its status.json is old
players() {
  local name=$1
  $SSH "state=\$(systemctl is-active ${SVC[$name]})
        if [ \"\$state\" = inactive ] || [ \"\$state\" = failed ]; then echo 0; exit 0; fi
        if [ \"\$state\" != active ]; then echo '?'; exit 0; fi
        s=\$(curl -s --max-time 5 http://127.0.0.1:${PORT[$name]}/status.json)
        if printf '%s' \"\$s\" | grep -q '\"PlayerCountStale\":true'; then echo '?'; exit 0; fi
        upd=\$(printf '%s' \"\$s\" | grep -o '\"UpdatedAt\":\"[^\"]*\"' | cut -d'\"' -f4)
        ts=\$(date -d \"\$upd\" +%s 2>/dev/null || echo 0)
        if [ \$(( \$(date +%s) - ts )) -gt 45 ]; then echo '?'; exit 0; fi
        c=\$(printf '%s' \"\$s\" | grep -o '\"PlayerCount\":[0-9]*' | cut -d: -f2)
        echo \"\${c:-?}\"" 2>/dev/null | grep -E '^([0-9]+|\?)$' || echo "?"
}

deploy_one() {
  local name=$1
  local backup="/opt/randomizer-anywhere/backup-$(date +%Y%m%d-%H%M%S)-$name"
  local result="$STAGE/result-$name"
  local unit="tmx-deploy-$name-$(date +%s)"

  $SSH "$REMOTE_SUDO rm -f $result && $REMOTE_SUDO systemd-run --quiet --collect --unit=$unit \
        /bin/bash $STAGE/deploy-remote.sh $STAGE/build ${DIR[$name]} ${SVC[$name]} ${PORT[$name]} $backup $result" \
    || { echo "FAILED could not start the server-side deploy"; return; }

  # The server does the work; just wait for its answer (it survives this connection dropping).
  local waited=0 answer=""
  while [ $waited -lt $RESULT_WAIT_SECONDS ]; do
    sleep 5; waited=$((waited + 5))
    answer=$($SSH "cat $result 2>/dev/null" 2>/dev/null || true)
    [ -n "$answer" ] && { echo "$answer"; return; }
  done
  echo "UNKNOWN no answer after ${RESULT_WAIT_SECONDS}s - check: journalctl -u $unit"
}

started=$(date +%s)
failures=0
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
        DEPLOYED_NOT_RUNNING*)      log "$name: new files installed (service is not running, so no restart)." ;;
        DEPLOYED*)                  log "$name: DEPLOYED. Backup of the replaced files: ${result#DEPLOYED }" ;;
        ROLLED_BACK_BUT_UNHEALTHY*) log "$name: ROLLED BACK, BUT THE OLD BUILD IS NOT HEALTHY EITHER - look at the server now."; failures=$((failures + 1)) ;;
        ROLLED_BACK*)               log "$name: ROLLED BACK - the new build did not come up healthy, the old one runs again."; failures=$((failures + 1)) ;;
        BUSY*)                      log "$name: not deployed right now (${result#BUSY}) - waiting for the next empty window."; STREAK[$name]=0; still+=("$name") ;;
        *)                          log "$name: unexpected answer - not retrying automatically."; failures=$((failures + 1)) ;;
      esac
    else
      still+=("$name")
    fi
  done
  pending=("${still[@]}")
  [ ${#pending[@]} -gt 0 ] && sleep $POLL_SECONDS
done

# keep the last 3 staging folders and the last 10 backups, so /opt doesn't fill up over the months
$SSH "cd /opt/randomizer-anywhere && ls -1d staging-* 2>/dev/null | sort | head -n -3 | xargs -r $REMOTE_SUDO rm -rf;
      ls -1d backup-* 2>/dev/null | sort | head -n -10 | xargs -r $REMOTE_SUDO rm -rf" \
  || log "(couldn't prune old staging/backup folders - harmless)"

if [ $failures -gt 0 ]; then
  log "Done, but $failures instance(s) were NOT deployed - see above."
  exit 3
fi
log "All done."
