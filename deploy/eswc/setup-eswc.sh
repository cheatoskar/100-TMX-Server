#!/bin/bash
# Sets up the TrackMania Nations ESWC server next to the running TMNF and TMUF ones. Run it ON THE
# SERVER, as root:
#
#     bash setup-eswc.sh
#
# It does NOT start anything and does NOT touch the other servers. What it does:
#   1. creates the system user tmxeswc (no login, no shell) - unlike TMNF/TMUF, this server does
#      NOT run as root: it is 2006 network code without updates, facing the internet
#   2. copies the application build (not the TMNF data) into /opt/randomizer-anywhere-eswc/publish
#   3. writes that folder's config.toml from config.toml next to this script, copying PublicHost
#      and the Discord webhooks (NOT AdminLogins - see config.toml) from the TMNF config, and generates the password
#      of the master server account (the master server creates the account on the first start)
#   4. installs the systemd unit 100tmx-eswc (not enabled, not started)
#   5. opens the firewall on the box itself (iptables) for 2352/3452 (TCP+UDP) and 8092 (TCP)
# Then it tells you the last steps. Safe to run twice: it refuses to overwrite an existing config.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC=/opt/randomizer-anywhere/publish
DST=/opt/randomizer-anywhere-eswc/publish
UNIT=/etc/systemd/system/100tmx-eswc.service
USER_NAME=tmxeswc

[ "$(id -u)" = 0 ] || { echo "run as root (sudo bash setup-eswc.sh)"; exit 1; }
[ -f "$SRC/RandomizerAnywhere.dll" ] || { echo "TMNF build not found in $SRC"; exit 1; }
[ -f "$DST/config.toml" ] && { echo "$DST/config.toml already exists - already set up. Nothing changed."; exit 1; }

# the build must already contain the ESWC support (old-server mode, ServerNation, DownloadSha256)
grep -q "DownloadSha256" "$SRC/RandomizerAnywhere.dll" 2>/dev/null || \
  { echo "The TMNF server is still running a build WITHOUT the ESWC support. Deploy the new build first, then run this."; exit 1; }

echo "1/5 system user $USER_NAME..."
id "$USER_NAME" >/dev/null 2>&1 || useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$USER_NAME"

echo "2/5 copying the application build..."
mkdir -p "$DST/WebStatus"
rsync -a \
  --exclude '/Servers/' --exclude '/WebStatus/' --exclude '/config.toml' \
  --exclude '/leaderboard.json*' --exclude '/skipped-maps.json' --exclude '/impossible-maps.json' \
  --exclude '/CANNOT_SEE_SERVER.txt' --exclude '/played-maps.json' --exclude '/MapCache/' \
  --exclude '/*.bak' --exclude '/*.bak-*' --exclude '/*.tmp' --exclude '/*.corrupt-*' \
  "$SRC/" "$DST/"
cp "$SRC/WebStatus/discord.png" "$SRC/WebStatus/favorite.png" "$DST/WebStatus/" 2>/dev/null || true

echo "3/5 writing config.toml..."
cp "$HERE/config.toml" "$DST/config.toml"
for key in PublicHost DiscordWebhookUrl DiscordWebhookUrlHard; do
  line="$(grep -E "^$key *=" "$SRC/config.toml" | head -1 || true)"
  if [ -n "$line" ]; then
    # replace the placeholder line; '|' as sed delimiter, escape & and \ in the copied value
    esc="$(printf '%s' "$line" | sed -e 's/[\\&|]/\\&/g')"
    sed -i -E "s|^$key *=.*|$esc|" "$DST/config.toml"
  fi
done
# letters and digits only: no quoting trouble in TOML or in the server's XML config
password="$(tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 20)"
sed -i -E "s|^ServerPassword *=.*|ServerPassword = \"$password\"|" "$DST/config.toml"
unset password

# The program files stay root's (read-only for the server); the folder itself and what the
# controller writes at runtime (status, leaderboard, lists, the downloaded game server) are its own.
chown root:root "$DST"/*.dll "$DST"/*.json 2>/dev/null || true
chown "$USER_NAME:$USER_NAME" "$DST" "$DST/WebStatus" "$DST/config.toml"
chmod 600 "$DST/config.toml"   # holds the server account password and the webhooks
chmod 755 "$DST/.."

echo "4/5 installing the systemd unit (not enabled, not started)..."
sed "s|^\[Service\]|[Service]\nUser=$USER_NAME\nGroup=$USER_NAME|" "$HERE/100tmx-eswc.service" > "$UNIT"
systemctl daemon-reload

echo "5/5 firewall on the box (iptables)..."
open_port() {
  local proto=$1 port=$2
  iptables -C INPUT -p "$proto" --dport "$port" -j ACCEPT 2>/dev/null && return 0
  # before Oracle's final REJECT rule, so it actually applies
  local reject_line
  reject_line=$(iptables -L INPUT --line-numbers -n | awk '$2=="REJECT" {print $1; exit}')
  if [ -n "$reject_line" ]; then
    iptables -I INPUT "$reject_line" -p "$proto" --dport "$port" -j ACCEPT
  else
    iptables -A INPUT -p "$proto" --dport "$port" -j ACCEPT
  fi
  echo "   opened $proto $port"
}
open_port tcp 2352; open_port udp 2352
open_port tcp 3452; open_port udp 3452
open_port tcp 8092
command -v netfilter-persistent >/dev/null && netfilter-persistent save >/dev/null && echo "   saved (netfilter-persistent)"

cat <<EOF

Done. The ESWC server is prepared but NOT running.

Last steps:
  1. Oracle Cloud console: add ingress rules to the instance's VCN security list
       TCP + UDP 2352,  TCP + UDP 3452,  TCP 8092   (source 0.0.0.0/0)
     XML-RPC 5002 stays closed - it is only for the controller on this box.
  2. Put the admins' ESWC logins into AdminLogins (nano $DST/config.toml) - until then nobody is admin.
  3. Start it:
       systemctl enable --now 100tmx-eswc
  4. Watch it come up (first start downloads the 2006 server, ~8 MB, and checks its SHA-256):
       journalctl -u 100tmx-eswc -f
     and the game server's own log for the master server login:
       ls -t $DST/Servers/TM/TmDedicatedServer/Logs/ | head -1
     The status page is then at http://<server>:8092/

Server account: login 100tmx_eswc, password in $DST/config.toml (the only copy - back it up).
The TMNF and TMUF servers are untouched.
EOF
