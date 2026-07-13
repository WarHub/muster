using System.Text.Json;
using BattleScribeSpec;
using Muster.Cli.Converters;
using Muster.Cli.Engines;
using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

/// <summary>
/// Integration coverage for the real promotion chain: <see cref="ReplyRenderer.Render"/> emits a
/// GitHub-comment reply with an embedded snapshot &#8594; <see cref="SnapshotExtractor.ExtractLatest"/>
/// pulls the snapshot back out of raw <c>gh api</c> comments JSON &#8594; <see cref="SpecRePinner.RePin"/>
/// replays it against the local data repo and re-pins it to the engine's current values. Nothing
/// exercised this end-to-end chain before — unit tests covered each stage against hand-written
/// fixtures of the *next* stage's expected input, which would not have caught a format drift
/// between what one stage emits and what the next expects.
/// </summary>
public class PromoteChainTests
{
    [Fact]
    public void Render_then_extract_then_repin_round_trips_and_pins_the_engines_actual_value()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();

        // Build a roster that replays cleanly against TestRepoFactory's data (fe-army /
        // cat-test / se-unit), with an observed roster-level cost pinned WRONG (999) — as if
        // captured from a stale/buggy original report — via the real SpecEmitter, exactly as
        // `muster report` would produce it.
        var roster = new ReplayRoster(
            Name: "Test",
            GameSystemId: "gs-test",
            ObservedTotals: [new ReplayCost(Name: "pts", TypeId: "ct-pts", Value: 999m)],
            BooksRevisions: [],
            Forces:
            [
                new ReplayForce(
                    ForceEntryId: "fe-army",
                    CatalogueId: "cat-test",
                    Selections: [new ReplaySelection(EntryId: "se-unit", Count: 1, CustomName: null, ObservedCosts: [], Children: [])],
                    ChildForces: [])
            ],
            Unmapped: []);

        var specYaml = SpecEmitter.Emit(roster, specId: "report", dataSource: "github:muster-e2e/test-data@main", pinObserved: true);

        // Render the REAL reply, exactly as `muster report` would post it as an issue comment.
        var verdict = new Verdict(VerdictKind.Confirmed, EngineGap: false, Labels: ["confirmed"], Detail: null);
        var rendered = ReplyRenderer.Render(
            verdict, roster: null, runs: null, specYaml: specYaml, problem: "Cost seems off", expected: "20 points");

        // Wrap as the raw `gh api /repos/{o}/{r}/issues/{n}/comments` JSON shape.
        var commentsJson = JsonSerializer.Serialize(new[] { new { body = rendered } });

        var extracted = SnapshotExtractor.ExtractLatest(commentsJson);
        Assert.NotNull(extracted);

        var engineSpec = EngineSpec.Parse(EngineSpec.BuiltinName);
        var rePinned = SpecRePinner.RePin(extracted!, dataDir, engineSpec, newSpecId: "report-issue-42");

        var loaded = SpecLoader.LoadFromYaml(rePinned);
        Assert.Equal("report-issue-42", loaded.Id);

        var pinStep = Assert.Single(loaded.Steps, s => s.ExpectedState is not null);
        var costs = pinStep.ExpectedState!.Costs;
        Assert.NotNull(costs);
        var pts = Assert.Single(costs!, c => c.TypeId == "ct-pts");

        // The engine's ACTUAL current value (20, per TestRepoFactory's data), not the stale
        // observed-from-report value (999) that was baked into the rendered snapshot.
        Assert.Equal(20m, pts.Value);
    }
}
