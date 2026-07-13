using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class ReplyRendererTests
{
    private static readonly Verdict NeedsInfo = new(
        VerdictKind.NeedsInfo, EngineGap: false, Labels: ["needs-info"], Detail: "no roster found in the report");

    [Fact]
    public void Omits_the_whole_snapshot_section_when_there_is_no_spec_at_all()
    {
        // F1c: an empty ```yaml block must never be rendered — the sticky comment is posted
        // with edit-mode: replace, so an empty snapshot here would destroy any durable snapshot
        // a prior evaluation had stored, permanently blocking promotion.
        var reply = ReplyRenderer.Render(
            NeedsInfo, roster: null, runs: null, specYaml: "", problem: null, expected: null);

        Assert.DoesNotContain("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("```yaml", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<details>", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_a_carried_forward_snapshot_with_a_visible_warning_note()
    {
        var reply = ReplyRenderer.Render(
            NeedsInfo, roster: null, runs: null, specYaml: "id: \"report\"\nsteps: []\n",
            problem: null, expected: null, carriedForwardSnapshot: true);

        Assert.Contains(
            "snapshot preserved from a previous evaluation", reply, StringComparison.Ordinal);
        Assert.Contains("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.Contains("id: \"report\"", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_render_the_carried_forward_note_for_a_freshly_derived_spec()
    {
        var confirmed = new Verdict(VerdictKind.Confirmed, EngineGap: false, Labels: ["confirmed"], Detail: null);
        var reply = ReplyRenderer.Render(
            confirmed, roster: null, runs: null, specYaml: "id: \"report\"\nsteps: []\n",
            problem: null, expected: null, carriedForwardSnapshot: false);

        Assert.DoesNotContain("snapshot preserved from a previous evaluation", reply, StringComparison.Ordinal);
        Assert.Contains("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
    }
}
