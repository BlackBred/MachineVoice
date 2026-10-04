#!/bin/bash
# Forwards one Cursor hook event to MachineVoice.
# The daemon stores the event before acknowledging it. If it is down, the same
# envelope is written to inbox/ and applied the next time MachineVoice starts.
set -u

ROOT="$(cd "$(dirname "$0")" && pwd)"
SOCK="$ROOT/ingest.sock"
INBOX="$ROOT/inbox"

input=$(cat)
envelope=$(printf '{"version":1,"type":"hook","source":"cursor","payload":%s}' "$input")

deliver() {
  [ -S "$SOCK" ] || return 1
  local code
  code=$(printf '%s' "$envelope" | curl --silent --show-error --max-time 2 \
      --connect-timeout 1 \
      --unix-socket "$SOCK" \
      --header 'Content-Type: application/json' \
      --header 'Expect:' \
      --data-binary @- \
      --output /dev/null \
      --write-out '%{http_code}' \
      http://localhost/hook) || code="000"
  case "$code" in
    200) return 0 ;;
    4??) return 0 ;;
    *) return 1 ;;
  esac
}

if ! deliver; then
  umask 077
  mkdir -p "$INBOX"
  stamp=$(perl -MTime::HiRes=time -e 'printf "%020d-%05d-%d", int(time()*1000000), int(rand(100000)), $$' 2>/dev/null || printf '%s-%s' "$(date -u +%Y%m%dT%H%M%S)" "$$")
  tmp="$INBOX/$stamp.json.tmp"
  printf '%s\n' "$envelope" > "$tmp"
  mv "$tmp" "$INBOX/$stamp.json"
fi

printf '%s\n' '{"continue":true}'
exit 0
