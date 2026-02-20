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
    private readonly string? _governanceAddress;

    public SanctionsList() { }

    /// <summary>
    /// Create a SanctionsList with governance access control.
    /// Only the governance address can add/remove sanctions (COMPL-04).
    /// </summary>
    public SanctionsList(byte[] governanceAddress)
    {
        _governanceAddress = ToHex(governanceAddress);
    }

    /// <summary>
    /// Add an address to the sanctions list.
    /// Requires governance authorization (COMPL-04).
    /// </summary>
    public bool AddSanction(byte[] address, string reason, byte[]? caller = null)
    {
        lock (_lock)
        {
            if (_governanceAddress != null && (caller == null || ToHex(caller) != _governanceAddress))
                return false;

            _sanctioned.Add(ToHex(address));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AddressBlocked,
                Subject = address,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Sanctioned: {reason}",
            });

            return true;
        }
    }

    /// <summary>
    /// Remove an address from the sanctions list.
    /// Requires governance authorization (COMPL-04).
    /// </summary>
    public bool RemoveSanction(byte[] address, string reason, byte[]? caller = null)
    {
        lock (_lock)
        {
            if (_governanceAddress != null && (caller == null || ToHex(caller) != _governanceAddress))
                return false;

            _sanctioned.Remove(ToHex(address));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AddressUnblocked,
                Subject = address,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Unsanctioned: {reason}",
            });

            return true;
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
