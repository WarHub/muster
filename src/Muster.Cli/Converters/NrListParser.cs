using System.Text.Json;

namespace Muster.Cli.Converters;

/// <summary>
/// Parses a New Recruit shared-list JSON payload into a ReplayRoster.
/// Node classification (verified against real NR data 2026-07-13):
/// army.options[] level 1 = catalogue; its children carrying "catalogue_id"
/// are forces; below that, nodes with "amount" are selections (entry id =
/// "link_id::option_id" when linked), nodes without are transparent
/// containers (categories / selection-entry-groups).
/// </summary>
public static class NrListParser
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 20_000;
    private const int MaxSelections = 5_000;

    public static ReplayRoster Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException e)
        {
            throw new FormatException($"not a valid New Recruit list: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("army", out var army))
                throw new FormatException("not a New Recruit list: missing 'army'");

            var name = GetString(root, "name") ?? "unnamed roster";
            var totals = ParseCosts(root, "totalCosts");

            List<string> revisions;
            if (root.TryGetProperty("books_revision", out var revs) && revs.ValueKind == JsonValueKind.Array)
            {
                revisions = revs.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetString()!)
                    .ToList();
            }
            else
            {
                revisions = [];
            }

            var gameSystemId = GetString(root, "bsid_system");

            var state = new ParseState();
            var forces = new List<ReplayForce>();
            foreach (var catNode in Options(army))
            {
                state.CountNode();
                var catalogueId = GetString(catNode, "option_id")
                    ?? throw new FormatException("catalogue node missing option_id");
                foreach (var child in Options(catNode))
                {
                    state.CountNode();
                    if (child.TryGetProperty("catalogue_id", out _))
                        forces.Add(ParseForce(child, catalogueId, state));
                    else
                        state.Unmapped.Add($"unexpected non-force node '{GetString(child, "name")}' under catalogue");
                }
            }

            if (forces.Count == 0)
                throw new FormatException("no forces found in the list");

            return new(name, gameSystemId, totals, revisions, forces, state.Unmapped);
        }
    }

    private sealed class ParseState
    {
        public List<string> Unmapped { get; } = [];
        public int Selections;
        private int _nodes;

        public void CountNode()
        {
            if (++_nodes > MaxNodes) throw new FormatException($"list too large (over {MaxNodes} nodes)");
        }
    }

    private static ReplayForce ParseForce(JsonElement node, string catalogueId, ParseState state)
    {
        var forceEntryId = GetString(node, "option_id") ?? throw new FormatException("force node missing option_id");
        var selections = new List<ReplaySelection>();
        CollectSelections(node, selections, state, depth: 0);
        return new(forceEntryId, catalogueId, selections, ChildForces: []);
    }

    private static void CollectSelections(JsonElement node, List<ReplaySelection> into, ParseState state, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("list too deeply nested");
        foreach (var child in Options(node))
        {
            state.CountNode();
            if (child.TryGetProperty("amount", out var amountEl))
            {
                if (++state.Selections > MaxSelections)
                    throw new FormatException($"list too large (over {MaxSelections} selections)");
                var optionId = GetString(child, "option_id");
                if (optionId is null)
                {
                    state.Unmapped.Add($"selection '{GetString(child, "name")}' missing option_id");
                    continue;
                }
                var linkId = GetString(child, "link_id");
                var entryId = linkId is null ? optionId : $"{linkId}::{optionId}";
                var count = amountEl.ValueKind == JsonValueKind.Number ? amountEl.GetInt32() : 1;
                var children = new List<ReplaySelection>();
                CollectSelections(child, children, state, depth + 1);
                into.Add(new(entryId, count, GetString(child, "customName"), ObservedCosts: [], children));
            }
            else
            {
                CollectSelections(child, into, state, depth + 1);
            }
        }
    }

    private static IEnumerable<JsonElement> Options(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Array
            ? options.EnumerateArray() : [];

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<ReplayCost> ParseCosts(JsonElement el, string property)
    {
        var result = new List<ReplayCost>();
        if (el.TryGetProperty(property, out var costs) && costs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in costs.EnumerateArray())
            {
                var typeId = GetString(c, "typeId");
                if (typeId is null) continue;
                var value = c.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
                result.Add(new(GetString(c, "name") ?? typeId, typeId, value));
            }
        }
        return result;
    }
}
