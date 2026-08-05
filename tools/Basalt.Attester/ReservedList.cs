namespace Basalt.Attester;

/// <summary>
/// The set of labels this attester watches, read from the same published file used to reserve them.
///
/// Sharing one file with the reservation step is deliberate. An attester watching a different set from
/// the one that was reserved is the quiet failure here: nothing errors, some domain owner simply waits
/// forever for a record nobody is reading.
/// </summary>
public static class ReservedList
{
    /// <summary>
    /// Parses the list. Accepts either a bare label or the .com domain it came from, ignores blanks and
    /// comments, and reports anything it drops rather than dropping it quietly.
    /// </summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines, Action<string>? onSkipped = null)
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var label = line.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ? line[..^4] : line;
            if (label.Length == 0 || label.Contains('.'))
            {
                // Anything below the second level cannot be a reserved label, and trimming it down to one
                // would make the attester watch a name nobody reserved.
                onSkipped?.Invoke($"skipping '{line}': not a second-level .com domain");
                continue;
            }

            label = label.ToLowerInvariant();
            if (seen.Add(label))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>Reads and parses the list file.</summary>
    public static IReadOnlyList<string> Load(string path, Action<string>? onSkipped = null)
        => Parse(File.ReadAllLines(path), onSkipped);
}
