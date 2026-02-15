namespace Basalt.Compliance;

/// <summary>
/// On-chain sanctions blocklist.
/// Maintained by governance; prevents sanctioned addresses from participating in transfers.
/// </summary>
public sealed class SanctionsList
{
    private readonly HashSet<string> _sanctioned = new();
    private readonly List<ComplianceEvent> _auditLog = new();
    private readonly object _lock = new();

    /// <summary>
    /// Add an address to the sanctions list.
    /// </summary>
    public void AddSanction(byte[] address, string reason)
    {
        lock (_lock)
        {
            _sanctioned.Add(ToHex(address));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AddressBlocked,
                Subject = address,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Sanctioned: {reason}",
            });
        }
    }

    /// <summary>
    /// Remove an address from the sanctions list.
    /// </summary>
    public void RemoveSanction(byte[] address, string reason)
    {
        lock (_lock)
        {
            _sanctioned.Remove(ToHex(address));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AddressUnblocked,
                Subject = address,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Unsanctioned: {reason}",
            });
        }
    }

    /// <summary>
    /// Check if an address is on the sanctions list.
    /// </summary>
    public bool IsSanctioned(byte[] address)
    {
        lock (_lock)
            return _sanctioned.Contains(ToHex(address));
    }

    /// <summary>
    /// Get audit log for sanctions operations.
    /// </summary>
    public IReadOnlyList<ComplianceEvent> GetAuditLog()
    {
        lock (_lock)
            return _auditLog.ToList();
    }

    private static string ToHex(byte[] data) => Convert.ToHexString(data);
}
