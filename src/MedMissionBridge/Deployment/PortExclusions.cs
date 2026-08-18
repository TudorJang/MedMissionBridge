using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MedMissionBridge.Deployment;

public readonly record struct PortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;
}

/// <summary>
/// The TCP ranges Windows has reserved for Hyper-V, WinNAT and friends. The default
/// MWL port 11112 falls inside one of them on some laptops, and the resulting bind
/// failure looks exactly like "the software is broken" to an operator in the field.
/// Reading the reservations lets the bridge say which range is in the way and which
/// port to move to.
/// </summary>
public static class PortExclusions
{
    private static readonly Regex RangeLine = new(@"^\s*(\d{1,5})\s+(\d{1,5})\s*\*?\s*$",
        RegexOptions.Compiled);

    /// <summary>Parses `netsh int ipv4 show excludedportrange protocol=tcp`. Keys on the
    /// two numbers per line because the surrounding headers are localised.</summary>
    public static IReadOnlyList<PortRange> Parse(string netshOutput)
    {
        var ranges = new List<PortRange>();
        foreach (var line in netshOutput.Split('\n'))
        {
            var match = RangeLine.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var end = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (start < 1 || start > end || end > 65535) continue;
            ranges.Add(new PortRange(start, end));
        }
        return ranges;
    }

    public static bool IsExcluded(IReadOnlyList<PortRange> ranges, int port) =>
        ranges.Any(r => r.Contains(port));

    /// <summary>A port the operator can retype without losing the plot: 11112 becomes
    /// 12112, not 11205. Null when nothing is free, which cannot happen in practice.</summary>
    public static int? SuggestFreePort(IReadOnlyList<PortRange> ranges, int desired)
    {
        for (var candidate = desired + 1000; candidate <= 65535; candidate += 1000)
            if (!IsExcluded(ranges, candidate)) return candidate;
        for (var candidate = desired + 1; candidate <= 65535; candidate++)
            if (!IsExcluded(ranges, candidate)) return candidate;
        return null;
    }

    /// <summary>Empty on any failure — an unreadable reservation table degrades the
    /// diagnostic to a generic one, it never blocks startup.</summary>
    public static IReadOnlyList<PortRange> FromSystem()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh",
                "int ipv4 show excludedportrange protocol=tcp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) return [];
            return Parse(output);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
