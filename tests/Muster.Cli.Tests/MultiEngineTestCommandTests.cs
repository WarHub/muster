using Muster.Cli.Commands;
using Muster.Cli.Reporting;
using Xunit;

namespace Muster.Cli.Tests;

public class MultiEngineTestCommandTests
{
    private static string TestAdapterDll => TestPaths.TestAdapterDll;

    [Fact]
    public void Two_engines_produce_two_runs_and_governing_resolves_by_precedence()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();

        var multi = MultiRunReport.Run(dataDir, fixturesDir,
            engineSpecs: ["wham", $"fake=dotnet:{TestAdapterDll}"],
            governing: ["fake", "wham"]);

        Assert.Equal(2, multi.Runs.Count);
        Assert.Equal("fake", multi.Governing);
        Assert.Contains(multi.Runs, r => r.Engine == "wham");
        Assert.Contains(multi.Runs, r => r.Engine == "fake");
    }

    [Fact]
    public void Unavailable_engine_is_named_not_dropped()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();

        var multi = MultiRunReport.Run(dataDir, fixturesDir,
            engineSpecs: ["wham", "ghost=/no/such/adapter-xyz"],
            governing: ["ghost", "wham"]);

        Assert.Single(multi.Runs);
        Assert.Equal(["ghost"], multi.Unavailable);
        Assert.Equal("wham", multi.Governing); // ghost didn't run, precedence falls through
    }

    [Fact]
    public void Github_actions_output_renders_engine_matrix()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var multi = MultiRunReport.Run(dataDir, fixturesDir, ["wham"], ["wham"]);

        using var sw = new StringWriter();
        MultiRunReport.Write(multi, "github-actions", sw);
        var text = sw.ToString();

        Assert.Contains("wham", text, StringComparison.Ordinal);
        Assert.Contains("governing", text, StringComparison.OrdinalIgnoreCase);
    }
}
