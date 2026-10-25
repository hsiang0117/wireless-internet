#!/usr/bin/env bash
# Isolated fake-clock checks. Does not run ADB, Android commands, kernel flock or real VPNs.
set -eu
NEW_SOURCE=$(cygpath -u "$1")
OLD_SOURCE=
[ "${2:--}" = - ] || OLD_SOURCE=$(cygpath -u "$2")
RUN_ROOT=$(cygpath -u "$3")
TOKEN=11111111111111111111111111111111
OTHER=22222222222222222222222222222222
CHECKS=0
mkdir -p "$RUN_ROOT"
HOOKS="$RUN_ROOT/hooks.sh"

cat > "$HOOKS" <<'HOOKS'
hook_now() { IFS=' ' read -r hook_clock hook_rest < "$ROOT/uptime"; hook_clock=${hook_clock%%.*}; }
cat() { printf 'cat|%s\n' "$*" >> "$ROOT/events"; command cat "$@"; }
mv() { hook_now; printf 'mv|%s|%s\n' "$2" "$hook_clock" >> "$ROOT/events"; command mv "$@"; }
flock() {
    [ "$1" = -x ] || return 0
    hook_now
    printf 'lock|%s\n' "$hook_clock" >> "$ROOT/events"
    if [ "$hook_clock" -eq 1030 ] && [ ! -f "$ROOT/lock-hook-done" ]; then
        case "${GUARD_TEST_MODE:-}" in
            race-beat) printf '1030\n' > "$DIR/beat";;
            race-token) printf '%s\n' 22222222222222222222222222222222 > "$ROOT/current";;
            race-cancel) : > "$DIR/cancel"; printf 'CANCELLED\n' > "$DIR/result";;
        esac
        : > "$ROOT/lock-hook-done"
    fi
    return 0
}
settings() {
    case "$*" in
        'get secure always_on_vpn_app')
            if [ "${GUARD_TEST_MODE:-}" = restricted ]; then printf '%s\n' com.genymobile.gnirehtet; else printf 'null\n'; fi;;
        'get secure always_on_vpn_lockdown') printf '0\n';;
        *) return 92;;
    esac
}
am() {
    hook_now
    printf 'stop|%s\n' "$hook_clock" >> "$ROOT/events"
    [ "${GUARD_TEST_MODE:-}" = stop-fails ] || printf '0\n' > "$ROOT/vpn"
}
dumpsys() {
    IFS= read -r hook_vpn < "$ROOT/vpn"
    [ "$hook_vpn" = 1 ] || return 0
    printf '%s\n' '  NetworkAgentInfo{fixture ni{VPN CONNECTED extra: VPN:com.genymobile.gnirehtet}'
}
sleep() {
    [ "$1" = 1 ] || return 0
    hook_now
    hook_clock=$((hook_clock + 1))
    printf '%s.00 0.00\n' "$hook_clock" > "$ROOT/uptime"
    printf 'sleep|%s\n' "$hook_clock" >> "$ROOT/events"
    case "${GUARD_TEST_MODE:-}" in
        healthy)
            if [ "$(((hook_clock - 1000) % 5))" -eq 0 ]; then
                hook_reply=$(sh "$SELF" beat "$TOKEN" "$TIMEOUT") || exit 93
                [ "$hook_reply" = "OK $TOKEN" ] || exit 94
                printf 'beat|%s\n' "$hook_clock" >> "$ROOT/events"
            fi
            if [ "$hook_clock" -ge 1060 ]; then
                printf '0\n' > "$ROOT/vpn"
                hook_reply=$(sh "$SELF" cancel "$TOKEN" "$TIMEOUT") || exit 95
                [ "$hook_reply" = "CANCELLED $TOKEN" ] || exit 96
            fi;;
        before-deadline) [ "$hook_clock" -lt 1029 ] || : > "$DIR/cancel";;
        race-beat) [ "$hook_clock" -lt 1031 ] || : > "$DIR/cancel";;
        early-token) [ "$hook_clock" -lt 1010 ] || printf '%s\n' 22222222222222222222222222222222 > "$ROOT/current";;
        rollback) printf '999.00 0.00\n' > "$ROOT/uptime";;
    esac
}
if [ "$ACTION" = fixture-read ]; then read_file "$ROOT/read-input"; exit $?; fi
HOOKS

check() {
    if ! "$@"; then printf 'FAIL check %s: %s\n' "$((CHECKS + 1))" "$*" >&2; exit 1; fi
    CHECKS=$((CHECKS + 1))
}
new_case() {
    CASE_ROOT="$RUN_ROOT/$1"
    SELF="$CASE_ROOT/$TOKEN/control.sh"
    mkdir -p "$CASE_ROOT/$TOKEN"
    awk -v hooks="$HOOKS" '
        /^ROOT=\/data\/local\/tmp\/codex-wireless-internet$/ { print "ROOT=${GUARD_TEST_ROOT:?}"; next }
        { gsub(/\/proc\/uptime/, "\"$ROOT/uptime\"") }
        /^case "\$ACTION" in$/ { while ((getline hook < hooks) > 0) print hook; close(hooks) }
        { print }
    ' "$2" > "$SELF"
    printf '%s\n' "$TOKEN" > "$CASE_ROOT/current"
    printf '1000.00 0.00\n' > "$CASE_ROOT/uptime"
    printf '1\n' > "$CASE_ROOT/vpn"
    printf '1000\n' > "$CASE_ROOT/$TOKEN/beat"
    printf '1000\n' > "$CASE_ROOT/$TOKEN/watch_tick"
    printf '30\n' > "$CASE_ROOT/$TOKEN/timeout"
    printf 'ARMED\n' > "$CASE_ROOT/$TOKEN/result"
    printf '%s\n' "$$" > "$CASE_ROOT/$TOKEN/pid"
    : > "$CASE_ROOT/events"
    GUARD_TEST_ROOT="$CASE_ROOT"
    export GUARD_TEST_ROOT
    GUARD_TEST_MODE=
    export GUARD_TEST_MODE
}
action() {
    if OUTPUT=$(sh "$SELF" "$1" "$TOKEN" "${2:-30}" 2>&1); then STATUS=0; else STATUS=$?; fi
}
value() { local result; IFS= read -r result < "$1" || :; printf '%s' "$result"; }
count() { awk -F'|' -v kind="$1" '$1==kind {n++} END {print n+0}' "$CASE_ROOT/events"; }
ticks() { awk -F'|' '$1=="mv" && $2~/\/watch_tick$/ {n++} END {print n+0}' "$CASE_ROOT/events"; }

for which in new old; do
    SOURCE=$NEW_SOURCE
    if [ "$which" = old ]; then [ -n "$OLD_SOURCE" ] || continue; SOURCE=$OLD_SOURCE; fi
    new_case "$which-healthy" "$SOURCE"
    GUARD_TEST_MODE=healthy
    action watch
    check test "$STATUS" -eq 0
    check test "$(value "$CASE_ROOT/$TOKEN/result")" = CANCELLED
    check test "$(count beat)" -eq 12
    check test "$(count sleep)" -eq 60
    check test "$(count stop)" -eq 0
    CAT_COUNT=$(count cat); MV_COUNT=$(count mv); TICK_COUNT=$(ticks)
    printf 'METRIC %s healthy60s cat=%s mv=%s watch_tick=%s sleep=60 beat=12\n' "$which" "$CAT_COUNT" "$MV_COUNT" "$TICK_COUNT"
    if [ "$which" = new ]; then
        check test "$CAT_COUNT" -eq 0
        check test "$TICK_COUNT" -eq 12
        NEW_TICKS=$TICK_COUNT
    else
        check test "$CAT_COUNT" -gt 0
        check test "$TICK_COUNT" -eq 61
        check test "$NEW_TICKS" -lt "$TICK_COUNT"
    fi
done

new_case expiry "$NEW_SOURCE"
action watch
check test "$STATUS" -eq 0
check test "$(value "$CASE_ROOT/$TOKEN/result")" = STOPPED
check test "$(value "$CASE_ROOT/$TOKEN/stopped_at")" -eq 1030
check test "$(count sleep)" -eq 30
check test "$(count stop)" -eq 1
check test "$(ticks)" -eq 7

new_case not-early "$NEW_SOURCE"; GUARD_TEST_MODE=before-deadline
action watch
check test "$STATUS" -eq 0
check test "$(count stop)" -eq 0
check test "$(count sleep)" -eq 29

new_case lock-recheck "$NEW_SOURCE"; GUARD_TEST_MODE=race-beat
action watch
check test "$STATUS" -eq 0
check test "$(value "$CASE_ROOT/$TOKEN/beat")" -eq 1030
check test "$(count stop)" -eq 0

for mode in race-token early-token; do
    new_case "$mode" "$NEW_SOURCE"; GUARD_TEST_MODE=$mode
    action watch
    check test "$STATUS" -eq 0
    check test "$(value "$CASE_ROOT/$TOKEN/result")" = SUPERSEDED
    check test "$(value "$CASE_ROOT/current")" = "$OTHER"
    check test "$(count stop)" -eq 0
done

new_case cancel-under-lock "$NEW_SOURCE"; GUARD_TEST_MODE=race-cancel
action watch
check test "$STATUS" -eq 0
check test "$(value "$CASE_ROOT/$TOKEN/result")" = CANCELLED
check test "$(count stop)" -eq 0

new_case expired-beat "$NEW_SOURCE"
printf '1030.00 0.00\n' > "$CASE_ROOT/uptime"
action beat
check test "$STATUS" -eq 2
check test "$OUTPUT" = "REFUSED $TOKEN EXPIRED"
check test "$(value "$CASE_ROOT/$TOKEN/beat")" -eq 1000
check test "$(count stop)" -eq 1
action beat
check test "$STATUS" -eq 2
check test "$OUTPUT" = "REFUSED $TOKEN STOPPED"
check test "$(count stop)" -eq 1

new_case liveness "$NEW_SOURCE"
for age in 5 10; do
    printf '%s.00 0.00\n' "$((1000 + age))" > "$CASE_ROOT/uptime"
    action beat
    check test "$STATUS" -eq 0
    check test "$OUTPUT" = "OK $TOKEN"
done
printf '1011.00 0.00\n' > "$CASE_ROOT/uptime"
action beat
check test "$STATUS" -eq 2
check test "$OUTPUT" = "REFUSED $TOKEN GUARD_DEAD"
check test "$(value "$CASE_ROOT/$TOKEN/beat")" -eq 1010
printf '%s\n' 999999999 > "$CASE_ROOT/$TOKEN/pid"
printf '1011\n' > "$CASE_ROOT/$TOKEN/watch_tick"
action beat
check test "$STATUS" -eq 2
check test "$OUTPUT" = "REFUSED $TOKEN GUARD_DEAD"

new_case cancel "$NEW_SOURCE"
action cancel
check test "$STATUS" -eq 2
check test "$OUTPUT" = "VPN_STILL_ACTIVE $TOKEN"
check test ! -f "$CASE_ROOT/$TOKEN/cancel"
printf '0\n' > "$CASE_ROOT/vpn"
action cancel
check test "$STATUS" -eq 0
check test "$OUTPUT" = "CANCELLED $TOKEN"
action beat
check test "$STATUS" -eq 2
check test "$OUTPUT" = "REFUSED $TOKEN CANCELLED"

new_case old-stop "$NEW_SOURCE"
printf '%s\n' "$OTHER" > "$CASE_ROOT/current"
action stop
check test "$STATUS" -eq 2
check test "$(count stop)" -eq 0
check test "$(value "$CASE_ROOT/current")" = "$OTHER"
printf 'STOP_FAILED\n' > "$CASE_ROOT/$TOKEN/result"
action stop
check test "$STATUS" -eq 2
check test "$OUTPUT" = "STATE $TOKEN STOP_FAILED"

new_case rollback "$NEW_SOURCE"; GUARD_TEST_MODE=rollback
action watch
check test "$STATUS" -eq 0
check test "$(value "$CASE_ROOT/$TOKEN/result")" = STOPPED
check test "$(count stop)" -eq 1

for mode in restricted stop-fails; do
    new_case "$mode" "$NEW_SOURCE"; GUARD_TEST_MODE=$mode
    action watch
    check test "$STATUS" -eq 0
    if [ "$mode" = restricted ]; then
        check test "$(value "$CASE_ROOT/$TOKEN/result")" = RESTRICTIONS_ENABLED
        check test "$(count stop)" -eq 0
    else
        check test "$(value "$CASE_ROOT/$TOKEN/result")" = STOP_FAILED
        check test "$(count stop)" -eq 1
    fi
    action beat
    check test "$STATUS" -eq 2
    check test "$(value "$CASE_ROOT/$TOKEN/beat")" -eq 1000
done

new_case malformed-beat "$NEW_SOURCE"
printf '1000\n9999\n' > "$CASE_ROOT/$TOKEN/beat"
action beat
check test "$STATUS" -eq 2
check test "$(count stop)" -eq 0
action watch
check test "$STATUS" -eq 0
check test "$(count stop)" -eq 1

for which in new old; do
    SOURCE=$NEW_SOURCE
    if [ "$which" = old ]; then [ -n "$OLD_SOURCE" ] || continue; SOURCE=$OLD_SOURCE; fi
    new_case "$which-read" "$SOURCE"
    for content in '' 'plain' '  spaces and \backslash  ' $'line-one\nline-two' $'tail\n\n'; do
        printf '%s' "$content" > "$CASE_ROOT/read-input"
        expected=$(printf '%s' "$content")
        action fixture-read
        check test "$STATUS" -eq 0
        check test "$OUTPUT" = "$expected"
    done
    rm -f "$CASE_ROOT/read-input"
    action fixture-read
    check test "$STATUS" -ne 0
    check test -z "$OUTPUT"
done

printf 'PASS %s isolated phone-guard shell checks; fake clock/flock/Android commands, no phone or ADB.\n' "$CHECKS"
