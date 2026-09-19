#!/bin/bash
# Deploys the mod into a throwaway Vintage Story data path, boots the real
# dedicated server, runs a few console commands against it, and reports whether
# anything in the log came from us and went wrong.
#
#   scripts/server_test.sh [extra console commands...]
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/devenv.sh"

VS="${VINTAGE_STORY:-/opt/vs}"
DATA="${SM_TEST_DATA:-/tmp/vs-testdata}"
MOD="$DATA/Mods/shinimodori"
LOG="$DATA/Logs/server-main.log"

echo "== building"
dotnet build "$ROOT/Shinimodori.csproj" -c Release -v q --nologo || exit 1

echo "== deploying to $MOD"
mkdir -p "$MOD"
rm -rf "$MOD/assets"
cp "$ROOT/bin/Release/Shinimodori.dll" "$MOD/"
cp "$ROOT/modinfo.json" "$MOD/"
cp -r "$ROOT/assets" "$MOD/"

rm -f "$LOG"
mkdir -p "$DATA"

# Console commands: whatever the caller passed, then always stop.
CMDS=("$@")
{
  # Give the server time to reach RunGame before typing at it.
  sleep 55
  for c in "${CMDS[@]:-}"; do
    [ -n "$c" ] && { echo "$c"; sleep 3; }
  done
  sleep 3
  echo "/stop"
  sleep 10
} | (cd "$VS" && timeout 180 dotnet VintagestoryServer.dll --dataPath "$DATA" >/tmp/vs-server-stdout.txt 2>&1)

echo
echo "== our log lines"
grep -n "shinimodori" "$LOG" 2>/dev/null || echo "  (none)"

echo
echo "== errors and warnings"
if grep -iE "\[Error\]|\[Fatal\]" "$LOG" 2>/dev/null | grep -v "Server overloaded"; then :; else echo "  none"; fi

echo
echo "== command output"
grep -iE "rbd|Return by Death|journal=|blocks=" /tmp/vs-server-stdout.txt 2>/dev/null | head -30 || true

FAILED=$(grep -icE "\[Error\]|\[Fatal\]" "$LOG" 2>/dev/null || echo 0)
echo
echo "== $FAILED error line(s) in the server log"
exit 0
