using MedMissionBridge.Deployment;

namespace MedMissionBridge.Tests;

public class PortExclusionsTests
{
    // Verbatim from `netsh int ipv4 show excludedportrange protocol=tcp` on a laptop
    // where the MWL port failed to bind. The trailing '*' marks an administered
    // exclusion and the header wording is localised on non-English Windows, so the
    // parser has to key on the numbers rather than on any of the words.
    private const string NetshOutput = """

        Protocol tcp Port Exclusion Ranges

        Start Port    End Port
        ----------    --------
              2869        2869
             10004       10103
             11105       11204
             50000       50059     *

        * - Administered port exclusions.

        """;

    [Fact]
    public void parses_every_range_and_ignores_the_prose()
    {
        var ranges = PortExclusions.Parse(NetshOutput);

        Assert.Equal(4, ranges.Count);
        Assert.Equal(new PortRange(2869, 2869), ranges[0]);
        Assert.Equal(new PortRange(11105, 11204), ranges[2]);
        Assert.Equal(new PortRange(50000, 50059), ranges[3]);
    }

    [Fact]
    public void unparseable_output_yields_no_ranges_rather_than_throwing()
    {
        // netsh needs no elevation, but an access error or a future format change
        // must degrade to "cannot tell", never take startup down.
        Assert.Empty(PortExclusions.Parse(""));
        Assert.Empty(PortExclusions.Parse("The requested operation requires elevation."));
    }

    [Theory]
    [InlineData(11112, true)]   // the default MWL port, inside 11105-11204
    [InlineData(11105, true)]   // inclusive lower bound
    [InlineData(11204, true)]   // inclusive upper bound
    [InlineData(11104, false)]
    [InlineData(12112, false)]  // the port the README recommends instead
    public void reports_whether_a_port_is_reserved(int port, bool expected)
    {
        Assert.Equal(expected, PortExclusions.IsExcluded(PortExclusions.Parse(NetshOutput), port));
    }

    [Fact]
    public void suggests_a_free_port_that_keeps_the_familiar_suffix()
    {
        // 11112 -> 12112 rather than 11205: the operator has to retype this into the
        // X-ray software, and a port that still ends in 112 reads as the same setting.
        Assert.Equal(12112, PortExclusions.SuggestFreePort(PortExclusions.Parse(NetshOutput), 11112));
    }

    [Fact]
    public void falls_back_to_the_next_free_port_when_every_thousand_is_reserved()
    {
        var everyThousand = Enumerable.Range(0, 60)
            .Select(i => new PortRange(11112 + i * 1000, 11112 + i * 1000))
            .ToList();

        var suggested = PortExclusions.SuggestFreePort(everyThousand, 11112);

        Assert.NotNull(suggested);
        Assert.False(PortExclusions.IsExcluded(everyThousand, suggested!.Value));
    }
}
