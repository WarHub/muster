using Muster.Cli.Commands;
using Xunit;

namespace Muster.Cli.Tests;

[Collection("Console output tests")]
public class ReportCommandTests
{
    private static string GreenFixtureYaml(string fixturesDir) =>
        File.ReadAllText(Path.Combine(fixturesDir, "unit-costs-20.yaml"));

    private static string WriteIssueBody(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string NewOutDir() => Directory.CreateTempSubdirectory("muster-report-out-").FullName;

    private static async Task<int> RunCaptured(
        string issueBodyPath, string dataDir, string outDir,
        string dataSource = "local:.", string[]? engines = null, string[]? governing = null,
        string? previousReplyPath = null)
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            return await ReportCommand.Run(
                new FileInfo(issueBodyPath), new DirectoryInfo(dataDir), dataSource,
                engines ?? [], governing ?? [], new DirectoryInfo(outDir),
                previousReplyPath is null ? null : new FileInfo(previousReplyPath),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Inline_spec_with_matching_pins_is_confirmed()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var yaml = GreenFixtureYaml(fixturesDir);
        var issueBody = $"""
            ### Roster

            ```yaml
            {yaml}
            ```

            ### Problem

            Test Unit seems to cost more than it should.

            ### Expected

            Test Unit should cost less.
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        // The fixture's inline spec declares setup.dataSource: "github:muster-e2e/test-data@main"
        // itself (TestRepoFactory) — matching --data-source here so F3's hermeticity check
        // (inline dataSource must match the repo's own) doesn't reject it as a mismatch.
        var exit = await RunCaptured(bodyPath, dataDir, outDir, dataSource: "github:muster-e2e/test-data@main");

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.StartsWith("<!-- muster:report -->", reply, StringComparison.Ordinal);
        Assert.Contains("Verdict: Confirmed", reply, StringComparison.Ordinal);
        Assert.Contains("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.Contains("```yaml", reply, StringComparison.Ordinal);
        Assert.Contains("Test Unit seems to cost more", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"confirmed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"engineGap\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"governing\": \"wham\"", json, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(outDir, "snapshot.yaml")));
    }

    [Fact]
    public async Task Inline_spec_with_mismatched_pins_is_not_reproducible()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var yaml = GreenFixtureYaml(fixturesDir).Replace("value: 20", "value: 999", StringComparison.Ordinal);
        var issueBody = $"""
            ```yaml
            {yaml}
            ```
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir, dataSource: "github:muster-e2e/test-data@main");

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.Contains("Verdict: Not reproducible", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"not-reproducible\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_body_is_needs_info_and_exits_zero()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var bodyPath = WriteIssueBody("this issue has no roster, no yaml, nothing usable at all.");
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.StartsWith("<!-- muster:report -->", reply, StringComparison.Ordinal);
        Assert.Contains("Verdict: Needs info", reply, StringComparison.Ordinal);
        Assert.Contains("New Recruit share link", reply, StringComparison.Ordinal);
        Assert.Contains("no roster found", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outDir, "snapshot.yaml")));

        // F1c: no spec at all (neither fresh nor carried forward) must never render an empty
        // yaml block — the whole snapshot <details> section is omitted outright.
        Assert.DoesNotContain("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("```yaml", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<details>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_is_carried_forward_from_previous_reply_when_this_evaluation_finds_no_roster()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();

        // A previous reply, exactly as `muster report` would have rendered and posted it, with
        // a durable snapshot embedded (e.g. captured back when the report's New Recruit link
        // still resolved).
        var previousReply = """
            <!-- muster:report -->

            ## Verdict: Confirmed

            The reported values reproduce against current data.

            <details><summary>Executable spec (snapshot)</summary>
            <!-- muster:snapshot -->

            ```yaml
            id: "report"
            setup:
              dataSource: "local:."
            steps:
              - expectedState: {}
            ```

            </details>
            """;
        var previousReplyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".md");
        await File.WriteAllTextAsync(previousReplyPath, previousReply, TestContext.Current.CancellationToken);

        // This time around, the NR link rotted (or was edited away): conversion produces no
        // spec/roster of its own.
        var bodyPath = WriteIssueBody("the link stopped working, sorry — no roster info here now.");
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir, previousReplyPath: previousReplyPath);

        Assert.Equal(0, exit);

        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.Contains("Verdict: Needs info", reply, StringComparison.Ordinal);
        Assert.Contains("snapshot preserved from a previous evaluation", reply, StringComparison.Ordinal);
        Assert.Contains("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.Contains("id: \"report\"", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);

        var snapshotPath = Path.Combine(outDir, "snapshot.yaml");
        Assert.True(File.Exists(snapshotPath));
        var snapshot = await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken);
        Assert.Contains("id: \"report\"", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inline_spec_with_mismatched_dataSource_is_needs_info_and_runs_no_engine()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();

        // A hostile inline spec that declares its own dataSource instead of leaving it to the
        // repository's — if honored, this would let the runner read arbitrary
        // container-reachable paths (F3: inline-spec dataSource hermeticity hole).
        var issueBody = """
            ```yaml
            id: "report"
            setup:
              dataSource: "local:C:/"
            steps:
              - expectedState: {}
            ```
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir, dataSource: "github:x/y");

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.Contains("Verdict: Needs info", reply, StringComparison.Ordinal);
        Assert.Contains(
            "inline spec declares a dataSource that does not match this repository's data source",
            reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);

        // No spec ever made it to disk, and (implicitly) no engine ever ran against it: exit 0,
        // needs-info, and no snapshot.yaml written.
        Assert.False(File.Exists(Path.Combine(outDir, "snapshot.yaml")));
    }

    [Fact]
    public async Task Invalid_inline_yaml_is_needs_info_and_exits_zero()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var issueBody = """
            ```yaml
            steps:
              - action: notAnAction
                thisIsNot: validSpecYaml: [
            ```
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_data_dir_exits_2()
    {
        var bodyPath = WriteIssueBody("no roster here");
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, Path.Combine(Path.GetTempPath(), "no-such-muster-data-dir"), outDir);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Missing_issue_body_file_exits_2()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var outDir = NewOutDir();

        var exit = await RunCaptured(Path.Combine(Path.GetTempPath(), "no-such-issue-body.md"), dataDir, outDir);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Unwritable_out_dir_exits_2_without_throwing()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var bodyPath = WriteIssueBody("no roster here");

        // A file already occupies the path we'll ask ReportCommand to create as a directory —
        // Directory.CreateDirectory must fail, and that failure must be caught (exit 2), not thrown.
        var collidingPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(collidingPath, "occupied");

        var exit = await RunCaptured(bodyPath, dataDir, collidingPath);

        Assert.Equal(2, exit);
    }
}
