using Muster.Cli.Converters;
using Xunit;

namespace Muster.Cli.Tests.Converters;

public class NrListParserTests
{
    private static string SampleJson => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "nr-list-war-horde.json"));

    [Fact]
    public void Parses_war_horde_sample()
    {
        var roster = NrListParser.Parse(SampleJson);

        Assert.Equal("war horde", roster.Name);
        var pts = Assert.Single(roster.ObservedTotals);
        Assert.Equal(950m, pts.Value);
        Assert.Equal("51b2-306e-1021-d207", pts.TypeId);
        var force = Assert.Single(roster.Forces);
        Assert.Equal("bb9d-299a-ed60-2d8a", force.ForceEntryId);
        Assert.Equal("a55f-b7b3-6c65-a05f", force.CatalogueId);
        Assert.NotEmpty(force.Selections);
        Assert.Empty(roster.Unmapped);
        Assert.Contains("Xenos - Orks: 2", roster.BooksRevisions);
    }

    [Fact]
    public void Linked_selection_gets_composite_entry_id()
    {
        var roster = NrListParser.Parse(SampleJson);
        var all = Flatten(roster);
        // "Battle Size" selection: option_id 564e-fbc6-5266-3ea4 selected via link 7380-3e40-6ed6-b7cc
        Assert.Contains(all, s => s.EntryId == "7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4");
    }

    [Fact]
    public void Container_nodes_are_transparent()
    {
        var roster = NrListParser.Parse(SampleJson);
        var all = Flatten(roster);
        // "Configuration" (category node, no amount) must not appear as a selection…
        Assert.DoesNotContain(all, s => s.EntryId.Contains("4ac9-fd30-1e3d-b249", StringComparison.Ordinal));
        // …but "Incursion", nested category → selection → group → child, must
        Assert.Contains(all, s => s.EntryId.EndsWith("d62d-db22-4893-4bc0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"army":{"options":[]},"name":"x"}""")]
    public void Hostile_or_empty_input_throws_FormatException(string json) =>
        Assert.Throws<FormatException>(() => NrListParser.Parse(json));

    [Fact]
    public void Pathologically_nested_options_throws_FormatException()
    {
        var nested = """{"name":"n","option_id":"x","amount":1,"options":[]}""";
        for (var i = 0; i < 100; i++)
        {
            nested = $$"""{"name":"n","option_id":"x","amount":1,"options":[{{nested}}]}""";
        }

        var json = $$"""
        {
            "name": "deep",
            "army": {
                "options": [
                    {
                        "option_id": "cat-1",
                        "options": [
                            {
                                "option_id": "force-1",
                                "catalogue_id": "cat-1",
                                "options": [{{nested}}]
                            }
                        ]
                    }
                ]
            }
        }
        """;

        Assert.Throws<FormatException>(() => NrListParser.Parse(json));
    }

    private static List<ReplaySelection> Flatten(ReplayRoster r)
    {
        var result = new List<ReplaySelection>();
        void Walk(IEnumerable<ReplaySelection> sels)
        {
            foreach (var s in sels) { result.Add(s); Walk(s.Children); }
        }
        foreach (var f in r.Forces) Walk(f.Selections);
        return result;
    }
}
