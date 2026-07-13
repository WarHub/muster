using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class SnapshotExtractorTests
{
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
