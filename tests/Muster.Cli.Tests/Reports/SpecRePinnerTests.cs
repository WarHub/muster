using BattleScribeSpec;
using BattleScribeSpec.Roster;
using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class SpecRePinnerTests
{
    // Regression for the Critical finding: RewriteWithPins used to locate the observed-values
    // marker with an UNANCHORED text.IndexOf, then truncate from that line to EOF. A step whose
    // quoted field (e.g. customName, attacker-controlled via a roster name) merely CONTAINS the
    // marker text mid-line would trip the same truncation and silently drop every subsequent
    // step — data corruption, not just a cosmetic bug. The marker is only legitimate as a
    // whole, line-anchored `# observed values from the report` comment line; a quoted scalar
    // embedding that text mid-line must never be mistaken for it.
    [Fact]
    public void RewriteWithPins_does_not_truncate_on_marker_text_embedded_in_a_quoted_field()
    {
        var specYaml = """
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
              - action: setCustomization
                forceId: "${{ steps.force-1.forceId }}"
                selectionId: "${{ steps.sel-1.selectionId }}"
                customName: "PWNED # observed values from the report — attacker-controlled name"
              - action: selectEntry
                id: "sel-2"
                forceId: "${{ steps.force-1.forceId }}"
                entryId: "se-unit"
            """;

        var costs = new List<CostState> { new("pts", "ct-pts", 20m) };

        var rewritten = SpecRePinner.RewriteWithPins(specYaml, "new-id", costs, "wham");

        // The subsequent step (sel-2), which sits AFTER the malicious quoted field on the page,
        // must survive the rewrite — the marker-in-a-quoted-field must not be treated as the
        // real marker and truncate everything from that line onward.
        Assert.Contains("\"sel-2\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("PWNED", rewritten, StringComparison.Ordinal);

        // Must still be a loadable spec — the rewrite is validated by SpecRePinner itself, but
        // re-assert here as the regression signal (a truncated document usually fails to load,
        // or loads with steps silently missing).
        var loaded = SpecLoader.LoadFromYaml(rewritten);
        Assert.Equal("new-id", loaded.Id);
        Assert.Contains(loaded.Steps, s => s.Id == "sel-2");
        Assert.Single(loaded.Steps, s => s.ExpectedState is not null);
    }

    [Fact]
    public void RewriteWithPins_drops_a_genuine_line_anchored_marker_block()
    {
        var specYaml = """
            id: "report"
            category: "report"
            setup:
              dataSource: "github:muster-e2e/test-data@main"

            steps:
              - action: addForce
                id: "force-1"
                forceEntryId: "fe-army"
                catalogueId: "cat-test"

              # observed values from the report — PASS means the reported state reproduces
              - expectedState:
                  costs:
                    - typeId: "ct-pts"
                      value: 999
            """;

        var costs = new List<CostState> { new("pts", "ct-pts", 20m) };

        var rewritten = SpecRePinner.RewriteWithPins(specYaml, "new-id", costs, "wham");

        Assert.DoesNotContain("999", rewritten, StringComparison.Ordinal);
        var loaded = SpecLoader.LoadFromYaml(rewritten);
        var pinStep = Assert.Single(loaded.Steps, s => s.ExpectedState is not null);
        var pts = Assert.Single(pinStep.ExpectedState!.Costs!, c => c.TypeId == "ct-pts");
        Assert.Equal(20m, pts.Value);
    }
}
