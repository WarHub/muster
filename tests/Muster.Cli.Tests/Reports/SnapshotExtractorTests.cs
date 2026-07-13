using Muster.Cli.Converters;
using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class SnapshotExtractorTests
{
    [Fact]
    public void ExtractFromBody_pulls_the_snapshot_out_of_a_real_rendered_reply()
    {
        // Same scaffolding style as PromoteChainTests: render a REAL reply via
        // ReplyRenderer.Render (rather than a hand-written comment body) and extract straight
        // from that single body — this is exactly what ReportCommand does with a
        // --previous-reply file's raw text, which is never wrapped in the `gh api` comments
        // JSON array that ExtractLatest expects.
        var verdict = new Verdict(VerdictKind.Confirmed, EngineGap: false, Labels: ["confirmed"], Detail: null);
        var specYaml = """
            id: "report"
            setup:
              dataSource: "local:."
            steps:
              - expectedState: {}
            """;
        var rendered = ReplyRenderer.Render(
            verdict, roster: null, runs: null, specYaml: specYaml, problem: "Cost seems off", expected: "20 points");

        var extracted = SnapshotExtractor.ExtractFromBody(rendered);

        Assert.NotNull(extracted);
        Assert.Contains("id: \"report\"", extracted, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracts_yaml_from_latest_snapshot_comment()
    {
        var comments = """
            [
              {"body": "just a human comment"},
              {"body": "<!-- muster:report -->\nold reply\n<details><summary>Executable spec (snapshot)</summary>\n<!-- muster:snapshot -->\n\n```yaml\nid: \"old\"\n```\n\n</details>"},
              {"body": "<!-- muster:report -->\nnew reply\n<details><summary>Executable spec (snapshot)</summary>\n<!-- muster:snapshot -->\n\n```yaml\nid: \"new\"\nsteps: []\n```\n\n</details>"}
            ]
            """;
        var yaml = SnapshotExtractor.ExtractLatest(comments);
        Assert.NotNull(yaml);
        Assert.Contains("id: \"new\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("old", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"body": "no snapshot here"}]""")]
    [InlineData("not json")]
    public void Missing_snapshot_returns_null(string comments) =>
        Assert.Null(SnapshotExtractor.ExtractLatest(comments));
}
