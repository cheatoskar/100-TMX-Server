#!/bin/bash
# Sets up the TMUF server next to the running TMNF one. Run it ON THE SERVER, as root:
#
#     bash setup-tmuf.sh
#
# It does NOT start anything and does NOT touch the TMNF server. What it does:
#   1. copies the application build (not the TMNF data) from the TMNF folder into
#      /opt/randomizer-anywhere-tmuf/publish
#   2. writes that folder's config.toml from config.toml next to this script, copying
#      AdminLogins, PublicHost and the Discord webhooks over from the TMNF config
#   3. installs the systemd unit 100tmx-tmuf (not enabled, not started)
# Then it tells you the last steps. Safe to run twice: it refuses to overwrite an existing config.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC=/opt/randomizer-anywhere/publish
DST=/opt/randomizer-anywhere-tmuf/publish
UNIT=/etc/systemd/system/100tmx-tmuf.service

[ -f "$SRC/RandomizerAnywhere.dll" ] || { echo "TMNF build not found in $SRC"; exit 1; }
[ -f "$DST/config.toml" ] && { echo "$DST/config.toml already exists - already set up. Nothing changed."; exit 1; }

# the build must already contain the TMUF support (ServerPort/ServerLogin config keys)
grep -q "ServerP2PPort" "$SRC/RandomizerAnywhere.dll" 2>/dev/null || \
  { echo "The TMNF server is still running the OLD build. Deploy the new build first, then run this."; exit 1; }

echo "1/3 copying the application build..."
mkdir -p "$DST/WebStatus"
rsync -a \
  --exclude '/Servers/' --exclude '/WebStatus/' --exclude '/config.toml' \
  --exclude '/leaderboard.json' --exclude '/skipped-maps.json' --exclude '/impossible-maps.json' \
  --exclude '/CANNOT_SEE_SERVER.txt' --exclude '/played-maps.json' --exclude '/MapCache/' \
  "$SRC/" "$DST/"
cp "$SRC/WebStatus/discord.png" "$SRC/WebStatus/favorite.png" "$DST/WebStatus/"

echo "2/3 writing config.toml..."
cp "$HERE/config.toml" "$DST/config.toml"
for key in AdminLogins PublicHost DiscordWebhookUrl DiscordWebhookUrlHard; do
  line="$(grep -E "^$key *=" "$SRC/config.toml" | head -1 || true)"
  if [ -n "$line" ]; then
    # replace the placeholder line; '|' as sed delimiter, escape & and \ in the copied value
    esc="$(printf '%s' "$line" | sed -e 's/[\\&|]/\\&/g')"
    sed -i -E "s|^$key *=.*|$esc|" "$DST/config.toml"
  fi
done
chmod 600 "$DST/config.toml"   # holds the server account and the webhooks

echo "3/3 installing the systemd unit (not enabled, not started)..."
cp "$HERE/100tmx-tmuf.service" "$UNIT"
systemctl daemon-reload

cat <<EOF

Done. The TMUF server is prepared but NOT running.

Last steps:
  1. Open the Hetzner firewall for the TMUF server (only if you use one):
       TCP + UDP 2351,  TCP + UDP 3451,  TCP 8091
  2. Fill in the TMUF server account:
       nano $DST/config.toml        (ServerLogin, ServerPassword, ServerValidationKey)
  3. Start it:
       systemctl enable --now 100tmx-tmuf
  4. Watch it come up:
       journalctl -u 100tmx-tmuf -f
     The status page is then at http://<server>:8091/

The TMNF server on 2350/3450/5000/8090 is untouched.
EOF
