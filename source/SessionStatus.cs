using System;

namespace WirelessInternet
{
    public enum ConnectionState { Disconnected, Connected, Recovering, Unknown }
    public enum ProtectionState { Inactive, Healthy, Degraded, Unknown }
    public enum TelemetryState { Unavailable, Current, Stale }

    // Independent health axes. A failed traffic read never invalidates a recent
    // acknowledged connection; a successful traffic read never renews protection.
    public sealed class SessionHealth
    {
        public readonly ConnectionState Connection;
        public readonly ProtectionState Protection;
        public bool Healthy { get { return Connection == ConnectionState.Connected && Protection == ProtectionState.Healthy; } }

        SessionHealth(ConnectionState connection, ProtectionState protection)
        { Connection = connection; Protection = protection; }

        public static SessionHealth Assess(bool hasSession, bool relayOwned, bool monitorOwned,
            double acknowledgedHeartbeatAge, bool phoneVpnConfirmed)
        {
            if (!hasSession) return new SessionHealth(ConnectionState.Disconnected, ProtectionState.Inactive);
            bool knownAge = !Double.IsNaN(acknowledgedHeartbeatAge) && !Double.IsInfinity(acknowledgedHeartbeatAge) && acknowledgedHeartbeatAge >= 0;
            ProtectionState protection = !monitorOwned ? ProtectionState.Degraded : !knownAge ? ProtectionState.Unknown :
                acknowledgedHeartbeatAge <= 12 ? ProtectionState.Healthy : ProtectionState.Degraded;
            ConnectionState connection = relayOwned && (phoneVpnConfirmed || protection == ProtectionState.Healthy) ?
                ConnectionState.Connected : ConnectionState.Recovering;
            return new SessionHealth(connection, protection);
        }
    }
}
