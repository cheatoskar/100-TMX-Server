#!/bin/bash
# Sets up one TMF game server instance on a machine that runs other things too (here: a small
# VM that is also a home DNS server). Run as root on that machine:
#
#     sudo bash setup-game-host.sh <name> <game port> <p2p port> <xml-rpc port> <web port> <build dir> <public host>
#     e.g. sudo bash setup-game-host.sh tmnf2 2350 3450 5000 8090 /home/ubuntu/tmx-build 152.70.183.80
#
# Safe to run again: it only adds what is missing and never overwrites an existing config.toml.
# The service is installed but NOT started - it needs a server account first (ServerLogin and
# ServerPassword in config.toml), then: systemctl enable --now 100tmx-<name>
#
# Isolation, because the machine has other duties: the server runs as its own user without a
# login shell, sees the system read-only, can write only its own folder, gets no capabilities,
# has a memory cap, and is the first thing the kernel kills when memory runs out.
set -euo pipefail

if [ $# -ne 7 ]; then
  sed -n 2,7p "$0"
  exit 1
fi

NAME=$1 GAME_PORT=$2 P2P_PORT=$3 RPC_PORT=$4 WEB_PORT=$5 BUILD=$6 PUBLIC_HOST=$7
USER_NAME=tmxgame
DIR=/opt/tmx-$NAME/publish
SERVICE=100tmx-$NAME

[ "$(id -u)" = 0 ] || { echo "run as root (sudo)"; exit 1; }
[ -f "$BUILD/RandomizerAnywhere.dll" ] || { echo "no build in $BUILD"; exit 1; }

echo "== packages: .NET runtime, 32-bit libraries for the TMF server binary"
if ! dpkg --print-foreign-architectures | grep -qx i386; then
  dpkg --add-architecture i386
fi
apt-get update -q
apt-get install -y -q aspnetcore-runtime-10.0 libc6:i386 libstdc++6:i386 libgcc-s1:i386 curl

echo "== user $USER_NAME"
id "$USER_NAME" >/dev/null 2>&1 || useradd --system --home-dir /opt/tmx-home --no-create-home --shell /usr/sbin/nologin "$USER_NAME"

echo "== files in $DIR"
install -d -o "$USER_NAME" -g "$USER_NAME" -m 755 "/opt/tmx-$NAME" "$DIR"
cp -R --preserve=mode,timestamps "$BUILD/." "$DIR/"
# runtime data (logs, maps, status, leaderboard) is written by the service user into its folders
chown "$USER_NAME:$USER_NAME" "$DIR"
install -d -o "$USER_NAME" -g "$USER_NAME" "$DIR/WebStatus" "$DIR/Servers" "$DIR/MapCache"

if [ ! -f "$DIR/config.toml" ]; then
  cat > "$DIR/config.toml" <<EOF
Game = "TMNF"
TmxGame = "TMNF"
BindIP = ""
XmlRpcPort = $RPC_PORT
AutoSkipMode = "AuthorMedal"
TimeLimit = 0
ConnectionUploadRate = 10240
Preset = "100tmx_tmnf"
ServerName = "100% TMX Project ($NAME)"
ServerComment = "100% TMX RMC - random maps, first to finish wins. Type /info or /source in chat."
GameSettings = "MatchSettings/Randomizer.txt"
AutoStart = true
AdminLogins = ""
ReplayServerPort = $WEB_PORT
PublicHost = "$PUBLIC_HOST"
Lan = false
ServerPort = $GAME_PORT
ServerP2PPort = $P2P_PORT
# fill in a TMF server account (player.trackmaniaforever.com) - the service won't start without it
ServerLogin = ""
ServerPassword = ""

[DownloadUrls]
TMF = "http://files2.trackmaniaforever.com/TrackmaniaServer_2011-02-21.zip"
TM = "http://slig.free.fr/TM/dedicated/TmDedicatedServer_2006-05-30.zip"

# race maps only, never Unlimiter (vanilla clients can't load them)
[TmxQuery]
primarytype = 0
inunlimiter = 0
EOF
  chown "$USER_NAME:$USER_NAME" "$DIR/config.toml"
  chmod 640 "$DIR/config.toml"
  echo "   wrote $DIR/config.toml (fill in ServerLogin/ServerPassword, AdminLogins, ServerDisplayName)"
else
  echo "   config.toml exists - left as it is"
fi

echo "== service $SERVICE"
cat > "/etc/systemd/system/$SERVICE.service" <<EOF
[Unit]
Description=100% TMX Project ($NAME) - RandomizerAnywhere TMF dedicated server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER_NAME
Group=$USER_NAME
WorkingDirectory=$DIR
# no server account yet -> refuse to start instead of hammering the master server
ExecStartPre=/bin/sh -c 'grep -Eq "^ServerLogin *= *\\\\".+\\\\"" config.toml'
ExecStart=/usr/bin/dotnet $DIR/RandomizerAnywhere.dll --dedicated
Restart=always
RestartSec=5
RestartPreventExitStatus=78
LimitCORE=0
KillMode=control-group
Environment=HOME=/opt/tmx-$NAME

# this machine also serves DNS: the game server gives way first
MemoryHigh=300M
MemoryMax=400M
OOMScoreAdjust=500
CPUWeight=100

# isolation
NoNewPrivileges=yes
ProtectSystem=strict
ReadWritePaths=/opt/tmx-$NAME
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
RestrictSUIDSGID=yes
RestrictRealtime=yes
RestrictNamespaces=yes
LockPersonality=yes
CapabilityBoundingSet=
AmbientCapabilities=
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload

echo "== firewall (iptables, saved with netfilter-persistent)"
open_port() { # proto port
  if ! iptables -C INPUT -p "$1" -m state --state NEW -m "$1" --dport "$2" -j ACCEPT 2>/dev/null; then
    # insert before the final REJECT rule
    local pos
    pos=$(iptables -L INPUT --line-numbers -n | awk '$2 == "REJECT" {print $1; exit}')
    iptables -I INPUT "${pos:-1}" -p "$1" -m state --state NEW -m "$1" --dport "$2" -j ACCEPT
    echo "   opened $1 $2"
  fi
}
open_port tcp "$GAME_PORT"; open_port udp "$GAME_PORT"
open_port tcp "$P2P_PORT";  open_port udp "$P2P_PORT"
open_port tcp "$WEB_PORT"
netfilter-persistent save >/dev/null

echo
echo "Done. Next:"
echo "  1. Oracle security list: open $GAME_PORT tcp+udp, $P2P_PORT tcp+udp, $WEB_PORT tcp"
echo "  2. fill ServerLogin/ServerPassword in $DIR/config.toml"
echo "  3. systemctl enable --now $SERVICE"
