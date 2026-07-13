using BattleScribeSpec;
using Muster.Cli.Fixtures;
using Xunit;

namespace Muster.Cli.Tests;

public class FixtureLoaderTests
{
    [Fact]
    public void LoadDirectory_reads_spec_yaml_files()
    {
        var dir = Directory.CreateTempSubdirectory("muster-fixtures").FullName;
        File.WriteAllText(Path.Combine(dir, "smoke.yaml"), """
            id: smoke
            category: golden
            description: smoke fixture
            setup:
              dataSource: "github:BSData/wh40k-10e@v10.6.0"
            steps:
              - action: addForce
                id: add-force
                forceEntryId: TO-FILL-IN
            """);

        var fixtures = FixtureLoader.LoadDirectory(dir);

        var (path, spec) = Assert.Single(fixtures);
        Assert.Equal(Path.Combine(dir, "smoke.yaml"), path);
        Assert.Equal("smoke", spec.Id);
        Assert.Equal("github:BSData/wh40k-10e@v10.6.0", spec.Setup.DataSource);
    }

    [Fact]
    public void LoadDirectory_reads_multiple_files_in_ordinal_path_order()
    {
        var dir = Directory.CreateTempSubdirectory("muster-fixtures").FullName;
        File.WriteAllText(Path.Combine(dir, "b.yaml"), """
            id: b-fixture
            category: golden
            description: second fixture
            setup:
              dataSource: "local:somewhere"
            steps:
              - action: addForce
                id: add-force
                forceEntryId: TO-FILL-IN
            """);
        File.WriteAllText(Path.Combine(dir, "a.yaml"), """
            id: a-fixture
            category: golden
            description: first fixture
            setup:
              dataSource: "local:somewhere"
            steps:
              - action: addForce
                id: add-force
                forceEntryId: TO-FILL-IN
            """);

        var fixtures = FixtureLoader.LoadDirectory(dir);

        Assert.Equal(2, fixtures.Count);
        Assert.Equal("a-fixture", fixtures[0].Spec.Id);
        Assert.Equal("b-fixture", fixtures[1].Spec.Id);
    }

    [Fact]
    public void LoadDirectory_empty_directory_returns_empty_list()
    {
        var dir = Directory.CreateTempSubdirectory("muster-fixtures").FullName;

        var fixtures = FixtureLoader.LoadDirectory(dir);

        Assert.Empty(fixtures);
    }
}
