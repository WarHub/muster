using System.Text.Json;
using BattleScribeSpec;
using Muster.Cli.Commands;
using Xunit;

namespace Muster.Cli.Tests;

[Collection("Console output tests")]
public class PromoteCommandTests
{
    // Same shape as the green `unit-costs-20` fixture (TestRepoFactory), but with the
    // roster-level cost pinned to an obviously wrong value (999) — as if the original bug
    // report's reply snapshot had captured a stale/buggy observation. Promotion must replay
    // the action steps and re-pin to whatever the CURRENT engine actually returns (20), not
    // carry the stale 999 forward.
    private const string SnapshotWithWrongPin = """
        id: "report"
        category: "report"
        description: "Converted from bug report roster 'Test'"
        setup:
          dataSource: "github:muster-e2e/test-data@main"

        steps:
          - action: addForce
            id: "force-1"
            forceEntryId: "fe-army"
            catalogueId: "cat-test"
          - action: selectEntry
            id: "sel-1"
            forceId: "${{ steps.force-1.forceId }}"
            entryId: "se-unit"

          # observed values from the report — PASS means the reported state reproduces
          - expectedState:
              costs:
                - typeId: "ct-pts"
                  value: 999
        """;

    private static string WriteTemp(string content, string ext = ".txt")
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ext);
        File.WriteAllText(path, content);
        return path;
    }

    private static string CommentsJsonWithSnapshot(string snapshotYaml)
    {
        var body = $"""
            <!-- muster:report -->

            ## Verdict: Confirmed

            <details><summary>Executable spec (snapshot)</summary>
            <!-- muster:snapshot -->

            ```yaml
            {snapshotYaml}
            ```

            </details>
            """;
        return JsonSerializer.Serialize(new[] { new { body } });
    }

    private static async Task<(int Exit, string Err)> RunCaptured(
        string issueBodyPath, string commentsPath, string dataDir, int issueNumber, string outDir)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(errWriter);
            var exit = await PromoteCommand.Run(
                new FileInfo(issueBodyPath), new FileInfo(commentsPath), new DirectoryInfo(dataDir),
                issueNumber, [], [], new DirectoryInfo(outDir), TestContext.Current.CancellationToken);
            return (exit, errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Promotes_snapshot_repinning_to_current_engine_values()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var outDir = Directory.CreateTempSubdirectory("muster-promote-out-").FullName;
        var issueBodyPath = WriteTemp("### Roster\n\nirrelevant for promote", ".md");
        var commentsPath = WriteTemp(CommentsJsonWithSnapshot(SnapshotWithWrongPin), ".json");

        var (exit, _) = await RunCaptured(issueBodyPath, commentsPath, dataDir, 7, outDir);

        Assert.Equal(0, exit);
        var written = Path.Combine(outDir, "report-issue-7.yaml");
        Assert.True(File.Exists(written));

        var spec = SpecLoader.Load(written);
        Assert.Equal("report-issue-7", spec.Id);
        var pinStep = Assert.Single(spec.Steps, s => s.ExpectedState is not null);
        var costs = pinStep.ExpectedState!.Costs;
        Assert.NotNull(costs);
        var pts = Assert.Single(costs!, c => c.TypeId == "ct-pts");
        Assert.Equal(20m, pts.Value);
    }

    [Fact]
    public async Task Name_collision_appends_numeric_suffix()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var outDir = Directory.CreateTempSubdirectory("muster-promote-out-").FullName;
        File.WriteAllText(Path.Combine(outDir, "report-issue-7.yaml"), "pre-existing: true");
        var issueBodyPath = WriteTemp("irrelevant", ".md");
        var commentsPath = WriteTemp(CommentsJsonWithSnapshot(SnapshotWithWrongPin), ".json");

        var (exit, _) = await RunCaptured(issueBodyPath, commentsPath, dataDir, 7, outDir);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outDir, "report-issue-7-2.yaml")));
        // Original file (a non-spec placeholder) must not have been overwritten.
        Assert.Equal("pre-existing: true", File.ReadAllText(Path.Combine(outDir, "report-issue-7.yaml")));
    }

    [Fact]
    public async Task Comments_without_snapshot_exit_2_with_stderr_message()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var outDir = Directory.CreateTempSubdirectory("muster-promote-out-").FullName;
        var issueBodyPath = WriteTemp("irrelevant", ".md");
        var commentsPath = WriteTemp("""[{"body": "just a human comment, no spec here"}]""", ".json");

        var (exit, err) = await RunCaptured(issueBodyPath, commentsPath, dataDir, 7, outDir);

        Assert.Equal(2, exit);
        Assert.Contains("snapshot", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(outDir, "report-issue-7.yaml")));
    }
}
