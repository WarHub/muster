using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace Muster.Cli.Tests.Fakes;

/// <summary>
/// Configurable in-proc engine double. Every selection contributes
/// <c>ptsValue × count</c> to a single roster-level "pts" cost, so tests can
/// pin totals and simulate divergent engines by varying <c>ptsValue</c>.
/// </summary>
public sealed class FakeRosterEngine(decimal ptsValue = 20m) : IRosterEngine
{
    private readonly List<(string SelectionId, int Count)> _selections = [];
    private int _nextId;

    public List<(string FileName, string Content)>? ReceivedFiles { get; private set; }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        ReceivedFiles = [.. files];
        return [];
    }

    public ActionOutputs AddForce(string forceEntryId, string catalogueId) =>
        new() { ForceId = $"force-{_nextId++}" };

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
        new() { ForceId = $"force-{_nextId++}" };

    public void RemoveForce(string forceId) { }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        var id = $"sel-{_nextId++}";
        _selections.Add((id, 1));
        return new() { SelectionId = id, Selections = [] };
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
        SelectEntry(forceId, entryId);

    public void DeselectSelection(string forceId, string selectionId) =>
        _selections.RemoveAll(s => s.SelectionId == selectionId);

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        var i = _selections.FindIndex(s => s.SelectionId == selectionId);
        if (i < 0) throw new InvalidOperationException($"unknown selection: {selectionId}");
        _selections[i] = (selectionId, count);
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        var id = $"sel-{_nextId++}";
        _selections.Add((id, 1));
        return new() { SelectionId = id };
    }

    public ActionOutputs DuplicateForce(string forceId) => new() { ForceId = $"force-{_nextId++}" };

    public void SetCostLimit(string costTypeId, decimal value) { }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes) { }

    public RosterState GetRosterState()
    {
        var total = _selections.Sum(s => s.Count) * ptsValue;
        return new RosterState("roster", "gs", [], [new CostState("pts", "pts", total)], []);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

    public void Dispose() { }
}
