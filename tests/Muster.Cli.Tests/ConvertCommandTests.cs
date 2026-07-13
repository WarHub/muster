using BattleScribeSpec;
using Muster.Cli.Commands;
using Xunit;

namespace Muster.Cli.Tests;

public class ConvertCommandTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "nr-list-war-horde.json");

    [Fact]
    public async Task Converts_nr_json_file_to_spec_yaml()
    {
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml"));
        try
        {
            var exit = await ConvertCommand.Run(SamplePath, id: null, dataSource: "local:.", pinObserved: true, output: outFile, ct: TestContext.Current.CancellationToken);

            Assert.Equal(0, exit);
            var yaml = File.ReadAllText(outFile.FullName);
            var spec = SpecLoader.LoadFromYaml(yaml);
            Assert.Equal("nr-list-war-horde", spec.Id);
            Assert.Contains(spec.Steps, s => s.Action == "addForce");
            Assert.Contains(spec.Steps, s => s.ExpectedState is not null);
        }
        finally { outFile.Delete(); }
    }

    [Fact]
    public async Task Missing_file_exits_2()
    {
        var exit = await ConvertCommand.Run("no-such-file.ros", null, "local:.", true, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Non_allowlisted_url_exits_2()
    {
        var exit = await ConvertCommand.Run("https://evil.example/app/list/x", null, "local:.", true, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, exit);
    }
}
