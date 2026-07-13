using BattleScribeSpec.Roster;
using Muster.Cli.Tests.Fakes;
using Xunit;

namespace Muster.Cli.Tests.Fakes;

public class FakeRosterEngineTests
{
    [Fact]
    public void Costs_scale_with_selection_count_and_pts_value()
    {
        using var engine = new FakeRosterEngine(ptsValue: 30m);
        engine.SetupFromFiles([("a.gst", "<gameSystem/>")]);
        var force = engine.AddForce("fe-1", "cat-1");
        var sel = engine.SelectEntry(force.ForceId!, "se-1");
        engine.SetSelectionCount(force.ForceId!, sel.SelectionId!, 2);

        var state = engine.GetRosterState();

        var pts = Assert.Single(state.Costs);
        Assert.Equal("pts", pts.TypeId);
        Assert.Equal(60m, pts.Value); // 30 pts × count 2
    }
}
