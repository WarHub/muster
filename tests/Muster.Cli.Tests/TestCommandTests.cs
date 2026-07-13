using System.Text.Json;
using Xunit;

namespace Muster.Cli.Tests;

public class TestCommandTests
{
    private static (string DataDir, string FixturesDir) CreateTestRepo()
    {
        var root = Directory.CreateTempSubdirectory("muster-e2e").FullName;
        var dataRoot = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var fixtures = Directory.CreateDirectory(Path.Combine(root, "tests", "rosters")).FullName;

        // Data repo laid out to mirror DataSourceResolver's own github cache structure:
        // {dataDir}/github/{org}/{repo}/{ref}/*.gst,*.cat — see RepoDataSourceResolver.
        var cachedRepoDir = Directory.CreateDirectory(
            Path.Combine(dataRoot, "github", "muster-e2e", "test-data", "main")).FullName;

        File.WriteAllText(Path.Combine(cachedRepoDir, "system.gst"), """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <gameSystem id="gs-test" name="Test System" revision="1" battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/gameSystemSchema">
              <costTypes>
                <costType id="ct-pts" name="pts" defaultCostLimit="-1.0" hidden="false"/>
              </costTypes>
              <forceEntries>
                <forceEntry id="fe-army" name="Army" hidden="false"/>
              </forceEntries>
            </gameSystem>
            """);
        File.WriteAllText(Path.Combine(cachedRepoDir, "catalogue.cat"), """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <catalogue id="cat-test" name="Test Catalogue" revision="1" battleScribeVersion="2.03" gameSystemId="gs-test" gameSystemRevision="1" xmlns="http://www.battlescribe.net/schema/catalogueSchema">
              <selectionEntries>
                <selectionEntry id="se-unit" name="Test Unit" hidden="false" type="unit">
                  <costs>
                    <cost name="pts" typeId="ct-pts" value="20.0"/>
                  </costs>
                </selectionEntry>
              </selectionEntries>
            </catalogue>
            """);

        File.WriteAllText(Path.Combine(fixtures, "unit-costs-20.yaml"), """
            id: unit-costs-20
            category: golden
            description: Test Unit costs 20 points
            setup:
              dataSource: "github:muster-e2e/test-data@main"
            steps:
              - action: addForce
                id: add-army
                forceEntryId: fe-army
                catalogueId: cat-test
              - action: selectEntry
                forceId: ${{ steps.add-army.forceId }}
                entryId: se-unit
              - expectedState:
                  costs:
                    - typeId: ct-pts
                      value: 20
            """);

        return (dataRoot, fixtures);
    }

    [Fact]
    public async Task Test_command_passes_on_green_fixture()
    {
        var (data, fixtures) = CreateTestRepo();
        var reportPath = Path.Combine(Directory.CreateTempSubdirectory("muster-e2e-report").FullName, "report.json");

        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(capturedOut);
            exit = await Program.Main(["test", "--data", data, "--fixtures", fixtures, "--report", reportPath]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exit);

        // stdout: summary mode prints a per-fixture status line and a Results line with counts.
        var stdout = capturedOut.ToString();
        Assert.Contains("[PASS] unit-costs-20", stdout, StringComparison.Ordinal);
        Assert.Contains("Results: 1 passed, 0 failed, 0 inconclusive", stdout, StringComparison.Ordinal);

        // --report writes a JSON file with matching counts.
        Assert.True(File.Exists(reportPath));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("inconclusive").GetInt32());
    }

    [Fact]
    public async Task Test_command_fails_on_wrong_expectation()
    {
        var (data, fixtures) = CreateTestRepo();
        var fixture = Path.Combine(fixtures, "unit-costs-20.yaml");
        File.WriteAllText(fixture, File.ReadAllText(fixture).Replace("value: 20", "value: 999", StringComparison.Ordinal));
        var exit = await Program.Main(["test", "--data", data, "--fixtures", fixtures]);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Test_command_inconclusive_on_missing_data()
    {
        var (_, fixtures) = CreateTestRepo();
        var exit = await Program.Main(["test", "--data", "Z:\\nope", "--fixtures", fixtures]);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Test_command_inconclusive_when_data_source_not_populated_locally()
    {
        // The fixtures reference a dataSource, but --data points at an *empty* data repo
        // root (no github/muster-e2e/test-data/main cache dir). RepoDataSourceResolver.
        // IsPopulatedFor must gate this fixture off before RosterRunner ever calls
        // DataSourceResolver.Resolve (which would otherwise shell out to `git clone`).
        var (_, fixtures) = CreateTestRepo();
        var emptyDataDir = Directory.CreateTempSubdirectory("muster-e2e-empty-data").FullName;
        var exit = await Program.Main(["test", "--data", emptyDataDir, "--fixtures", fixtures]);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Test_command_inconclusive_not_failed_on_engine_harness_crash()
    {
        // Reviewer repro: a malformed .gst (unclosed XML tag) makes the engine throw during
        // Setup. RosterRunner catches it and sets SpecResult.HarnessError; TestCommand must
        // classify that fixture as inconclusive (exit 2), never as a genuine failure (exit 1).
        var (data, fixtures) = CreateTestRepo();
        var malformedGst = Directory
            .EnumerateFiles(data, "*.gst", SearchOption.AllDirectories)
            .Single();
        File.WriteAllText(malformedGst, """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <gameSystem id="gs-test" name="Test System" revision="1" battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/gameSystemSchema">
              <costTypes>
                <costType id="ct-pts" name="pts" defaultCostLimit="-1.0" hidden="false">
              </costTypes>
              <forceEntries>
                <forceEntry id="fe-army" name="Army" hidden="false"/>
              </forceEntries>
            </gameSystem>
            """);

        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(capturedOut);
            exit = await Program.Main(["test", "--data", data, "--fixtures", fixtures]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(2, exit);
        var stdout = capturedOut.ToString();
        Assert.Contains("[????] unit-costs-20", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[FAIL]", stdout, StringComparison.Ordinal);
        Assert.Contains("Results: 0 passed, 0 failed, 1 inconclusive", stdout, StringComparison.Ordinal);
    }
}
