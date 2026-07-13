using BattleScribeSpec;
using Muster.Cli.Converters;
using Xunit;

namespace Muster.Cli.Tests.Converters;

public class SpecEmitterTests
{
    private static ReplayRoster Sample() => new(
        Name: "war horde",
        GameSystemId: "sys-1",
        ObservedTotals: [new("pts", "51b2-306e-1021-d207", 950m)],
        BooksRevisions: ["Xenos - Orks: 2"],
        Forces:
        [
            new("bb9d-299a-ed60-2d8a", "a55f-b7b3-6c65-a05f",
                Selections:
                [
                    new("7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4", 1, null, [],
                        Children: [new("d62d-db22-4893-4bc0", 1, null, [], [])]),
                    new("boy-entry", 3, "Da Ladz", [new("pts", "pts-id", 60m)], []),
                ],
                ChildForces: []),
        ],
        Unmapped: []);

    [Fact]
    public void Emitted_yaml_round_trips_through_SpecLoader()
    {
        var yaml = SpecEmitter.Emit(Sample(), "issue-42", "github:test/repo", pinObserved: true);
        var spec = SpecLoader.LoadFromYaml(yaml); // throws on invalid spec

        Assert.Equal("issue-42", spec.Id);
        Assert.Equal("github:test/repo", spec.Setup.DataSource);
        Assert.Contains(spec.Steps, s => s.Action == "addForce" && s.ForceEntryId == "bb9d-299a-ed60-2d8a");
        Assert.Contains(spec.Steps, s => s.Action == "selectEntry" && s.EntryId == "7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4");
        Assert.Contains(spec.Steps, s => s.Action == "selectChildEntry" && s.EntryId == "d62d-db22-4893-4bc0");
        Assert.Contains(spec.Steps, s => s.Action == "setSelectionCount" && s.Count == 3);
        Assert.Contains(spec.Steps, s => s.Action == "setCustomization" && s.CustomName == "Da Ladz");
        var assertStep = spec.Steps.Last();
        Assert.NotNull(assertStep.ExpectedState);
        var cost = Assert.Single(assertStep.ExpectedState!.Costs!);
        Assert.Equal(950m, cost.Value);
        Assert.Equal("51b2-306e-1021-d207", cost.TypeId);
    }

    [Fact]
    public void Per_selection_observed_costs_are_commented_not_pinned()
    {
        // Decision (Task 7 Step 4): RosterRunner.AssertSelections matches expected vs.
        // actual selections positionally (expected[si] vs actual[si]), so a per-selection
        // expectedState pin keyed on the converter's emit order could silently drift onto
        // the wrong selection if the engine reorders/auto-adds selections. Observed
        // per-selection costs are therefore surfaced as a comment only, never pinned.
        var yaml = SpecEmitter.Emit(Sample(), "issue-42", "github:test/repo", pinObserved: true);

        Assert.Contains(
            "# per-selection costs observed but not pinned (comparer is order-sensitive)",
            yaml, StringComparison.Ordinal);

        var spec = SpecLoader.LoadFromYaml(yaml); // still must round-trip cleanly
        Assert.DoesNotContain(spec.Steps, s =>
            s.ExpectedState?.Forces is { Count: > 0 });
    }

    [Fact]
    public void No_comment_when_no_selection_has_observed_costs()
    {
        var roster = Sample() with
        {
            Forces =
            [
                new("bb9d-299a-ed60-2d8a", "a55f-b7b3-6c65-a05f",
                    Selections: [new("entry-1", 1, null, [], [])],
                    ChildForces: []),
            ],
        };
        var yaml = SpecEmitter.Emit(roster, "issue-42", "github:test/repo", pinObserved: true);
        Assert.DoesNotContain("per-selection costs observed", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_pins_no_expectedState_is_emitted()
    {
        var yaml = SpecEmitter.Emit(Sample(), "issue-42", "github:test/repo", pinObserved: false);
        var spec = SpecLoader.LoadFromYaml(yaml);
        Assert.DoesNotContain(spec.Steps, s => s.ExpectedState is not null);
    }

    [Fact]
    public void Yaml_special_characters_are_escaped()
    {
        var roster = Sample() with { Name = "list: with \"quotes\" & #hash" };
        var yaml = SpecEmitter.Emit(roster, "x", "local:.", pinObserved: true);
        Assert.NotNull(SpecLoader.LoadFromYaml(yaml)); // must not throw
    }

    [Fact]
    public void Names_with_control_characters_round_trip_through_SpecLoader()
    {
        const string tricky = "line1\nline2: evil";
        var roster = new ReplayRoster(
            Name: tricky,
            GameSystemId: "sys-1",
            ObservedTotals: [],
            BooksRevisions: [],
            Forces:
            [
                new ReplayForce("force-entry", "cat-1",
                    Selections:
                    [
                        new ReplaySelection("entry-1", 1, tricky + "\t", [], []),
                    ],
                    ChildForces: []),
            ],
            Unmapped: []);

        var yaml = SpecEmitter.Emit(roster, "x", "local:.", pinObserved: false);
        var spec = SpecLoader.LoadFromYaml(yaml); // must not throw (hard round-trip guarantee)

        var customStep = Assert.Single(spec.Steps, s => s.Action == "setCustomization");
        Assert.Equal(tricky + "\t", customStep.CustomName);
    }

    [Fact]
    public void Selection_steps_reference_parent_outputs()
    {
        var yaml = SpecEmitter.Emit(Sample(), "x", "local:.", pinObserved: false);
        Assert.Contains("${{ steps.sel-1.selectionId }}", yaml, StringComparison.Ordinal);
        Assert.Contains("${{ steps.force-1.forceId }}", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_and_selection_expressions_survive_round_trip_verbatim()
    {
        var yaml = SpecEmitter.Emit(Sample(), "x", "local:.", pinObserved: false);
        var spec = SpecLoader.LoadFromYaml(yaml);

        var selectEntryStep = Assert.Single(
            spec.Steps, s => s.Action == "selectEntry" && s.EntryId == "7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4");
        Assert.Equal("${{ steps.force-1.forceId }}", selectEntryStep.ForceId);

        var childStep = Assert.Single(spec.Steps, s => s.Action == "selectChildEntry");
        Assert.Equal("${{ steps.force-1.forceId }}", childStep.ForceId);
        Assert.Equal("${{ steps.sel-1.selectionId }}", childStep.SelectionId);
    }
}
