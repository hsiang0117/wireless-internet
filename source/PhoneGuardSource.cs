namespace WirelessInternet
{
    // Android-side guard source: compiled into the desktop worker, not a distributed script.
    internal static class PhoneGuardSource
    {
        internal const string Text = @"#!/system/bin/sh
# One-session guard. No settings, network switches, boot hooks or APK changes.
umask 077
ROOT=/data/local/tmp/codex-wireless-internet
ACTION=${1:-}
TOKEN=${2:-}
TIMEOUT=${3:-30}
case ""$TOKEN"" in ''|*[!0-9a-f]*) exit 64;; esac
[ ""${#TOKEN}"" -eq 32 ] || exit 64
case ""$TIMEOUT"" in ''|*[!0-9]*) exit 64;; esac
[ ""$TIMEOUT"" -ge 15 ] && [ ""$TIMEOUT"" -le 120 ] || exit 64
DIR=""$ROOT/$TOKEN""
SELF=""$DIR/control.sh""
[ ""$0"" = ""$SELF"" ] || exit 64
LOCKED=0
# Android mksh marks extra descriptors close-on-exec; fd 0 is inherited by flock.
# This is a non-interactive script, so its stdin can hold the kernel lock file.
exec 0<>""$ROOT/state.lock"" || exit 1

now_seconds() {
    read -r uptime rest < /proc/uptime
    echo ""${uptime%%.*}""
}
read_file() {
    line=
    while IFS= read -r line || [ -n ""$line"" ]; do
        printf '%s\n' ""$line""
    done 2>/dev/null < ""$1""
}
write_value() {
    printf '%s\n' ""$2"" > ""$1.tmp"" && mv ""$1.tmp"" ""$1"" </dev/null
}
lock_state() {
    attempts=0
    while ! flock -x -n 0; do
        attempts=$((attempts + 1))
        [ ""$attempts"" -lt 80 ] || return 1
        sleep 0.1
    done
    LOCKED=1
}
unlock_state() {
    [ ""$LOCKED"" -eq 0 ] || flock -u 0
    LOCKED=0
}
trap unlock_state EXIT
vpn_connected() {
    dumpsys connectivity </dev/null 2>/dev/null | grep '^  NetworkAgentInfo.*ni{VPN CONNECTED extra: VPN:com\.genymobile\.gnirehtet}' >/dev/null
}
guard_alive() {
    guard_pid=$(read_file ""$DIR/pid"")
    case ""$guard_pid"" in ''|*[!0-9]*) return 1;; esac
    kill -0 ""$guard_pid"" 2>/dev/null || return 1
    tick=$(read_file ""$DIR/watch_tick"")
    case ""$tick"" in ''|*[!0-9]*) return 1;; esac
    current=$(now_seconds)
    [ ""$current"" -ge ""$tick"" ] && [ ""$((current - tick))"" -le 10 ]
}
stop_vpn() {
    # Caller holds the phone lock. A stale guard must never stop a newer session.
    if [ ""$(read_file ""$ROOT/current"")"" != ""$TOKEN"" ]; then
        [ ""$(read_file ""$DIR/result"")"" != ARMED ] || write_value ""$DIR/result"" SUPERSEDED
        return 2
    fi
    always_on=$(settings get secure always_on_vpn_app </dev/null)
    lockdown=$(settings get secure always_on_vpn_lockdown </dev/null)
    if [ ""$always_on"" = com.genymobile.gnirehtet ] || { [ ""$lockdown"" != 0 ] && [ ""$lockdown"" != null ]; }; then
        write_value ""$DIR/result"" RESTRICTIONS_ENABLED
        return 3
    fi
    write_value ""$DIR/stop_reason"" ""$ACTION""
    am start -a com.genymobile.gnirehtet.STOP -n com.genymobile.gnirehtet/.GnirehtetActivity </dev/null >/dev/null 2>&1
    attempts=0
    while [ ""$attempts"" -lt 10 ]; do
        if ! vpn_connected; then
            write_value ""$DIR/result"" STOPPED
            write_value ""$DIR/stopped_at"" ""$(now_seconds)""
            [ ""$(read_file ""$ROOT/current"")"" != ""$TOKEN"" ] || rm -f ""$ROOT/current""
            return 0
        fi
        attempts=$((attempts + 1))
        sleep 0.5
    done
    write_value ""$DIR/result"" STOP_FAILED
    return 4
}

case ""$ACTION"" in
    arm)
        mkdir -p ""$DIR"" || exit 1
        lock_state || exit 1
        write_value ""$ROOT/current"" ""$TOKEN"" || exit 1
        write_value ""$DIR/beat"" ""$(now_seconds)"" || exit 1
        write_value ""$DIR/timeout"" ""$TIMEOUT"" || exit 1
        write_value ""$DIR/result"" ARMED || exit 1
        rm -f ""$DIR/cancel"" ""$DIR/ready""
        nohup setsid sh ""$SELF"" watch ""$TOKEN"" ""$TIMEOUT"" </dev/null >/dev/null 2>&1 &
        unlock_state
        attempts=0
        while [ ""$attempts"" -lt 30 ]; do
            if [ -f ""$DIR/ready"" ] && guard_alive; then echo ""ARMED $TOKEN"";exit 0;fi
            attempts=$((attempts + 1))
            sleep 0.1
        done
        echo ""GUARD_NOT_READY $TOKEN""
        exit 1
        ;;
    watch)
        trap '' HUP
        echo ""$$"" > ""$DIR/pid""
        last_watch_tick=$(now_seconds)
        write_value ""$DIR/watch_tick"" ""$last_watch_tick""
        : > ""$DIR/ready""
        while :; do
            if [ -f ""$DIR/cancel"" ]; then exit 0;fi
            if [ ""$(read_file ""$ROOT/current"")"" != ""$TOKEN"" ]; then
                [ ""$(read_file ""$DIR/result"")"" != ARMED ] || write_value ""$DIR/result"" SUPERSEDED
                exit 0
            fi
            [ ""$(read_file ""$DIR/result"")"" = ARMED ] || exit 0
            current=$(now_seconds)
            # Keep one-second deadline checks; only the liveness-file write is throttled.
            if [ ""$current"" -lt ""$last_watch_tick"" ] || [ ""$((current - last_watch_tick))"" -ge 5 ]; then
                write_value ""$DIR/watch_tick"" ""$current"" && last_watch_tick=$current
            fi
            beat=$(read_file ""$DIR/beat"")
            case ""$beat"" in ''|*[!0-9]*) beat=0;; esac
            if [ ""$current"" -lt ""$beat"" ] || [ ""$((current - beat))"" -ge ""$TIMEOUT"" ]; then
                lock_state || exit 1
                # A heartbeat may have arrived while waiting for the lock.
                current=$(now_seconds)
                beat=$(read_file ""$DIR/beat"")
                case ""$beat"" in ''|*[!0-9]*) beat=0;; esac
                if [ ! -f ""$DIR/cancel"" ] && [ ""$(read_file ""$DIR/result"")"" = ARMED ] && { [ ""$current"" -lt ""$beat"" ] || [ ""$((current - beat))"" -ge ""$TIMEOUT"" ]; }; then
                    stop_vpn
                    unlock_state
                    exit 0
                fi
                unlock_state
            fi
            sleep 1
        done
        ;;
    beat)
        lock_state || exit 1
        if [ ""$(read_file ""$ROOT/current"")"" != ""$TOKEN"" ] || [ ""$(read_file ""$DIR/result"")"" != ARMED ] || [ -f ""$DIR/cancel"" ]; then
            echo ""REFUSED $TOKEN $(read_file ""$DIR/result"")""
            exit 2
        fi
        current=$(now_seconds)
        beat=$(read_file ""$DIR/beat"")
        timeout=$(read_file ""$DIR/timeout"")
        case ""$beat"" in ''|*[!0-9]*) exit 2;; esac
        case ""$timeout"" in ''|*[!0-9]*) exit 2;; esac
        [ ""$timeout"" -ge 15 ] && [ ""$timeout"" -le 120 ] || exit 2
        if [ ""$current"" -lt ""$beat"" ] || [ ""$((current - beat))"" -ge ""$timeout"" ]; then
            stop_vpn
            echo ""REFUSED $TOKEN EXPIRED""
            exit 2
        fi
        if ! guard_alive; then echo ""REFUSED $TOKEN GUARD_DEAD"";exit 2;fi
        write_value ""$DIR/beat"" ""$current"" || exit 1
        echo ""OK $TOKEN""
        ;;
    cancel)
        if vpn_connected; then echo ""VPN_STILL_ACTIVE $TOKEN"";exit 2;fi
        lock_state || exit 1
        : > ""$DIR/cancel""
        write_value ""$DIR/result"" CANCELLED
        [ ""$(read_file ""$ROOT/current"")"" != ""$TOKEN"" ] || rm -f ""$ROOT/current""
        echo ""CANCELLED $TOKEN""
        ;;
    stop)
        lock_state || exit 1
        stop_vpn
        result=$?
        echo ""STATE $TOKEN $(read_file ""$DIR/result"")""
        exit ""$result""
        ;;
    status)
        state=$(read_file ""$DIR/result"")
        if [ ""$state"" = ARMED ] && [ ""$(read_file ""$ROOT/current"")"" = ""$TOKEN"" ] && guard_alive; then
            echo ""ACTIVE $TOKEN $(read_file ""$DIR/beat"") $(read_file ""$DIR/watch_tick"")""
        else
            echo ""STATE $TOKEN $state""
            exit 2
        fi
        ;;
    *) exit 64;;
esac
";
    }
}
