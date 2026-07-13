namespace Muster.Cli.Converters;

public sealed record ReplayCost(string Name, string TypeId, decimal Value);

public sealed record ReplaySelection(
    string EntryId, int Count, string? CustomName,
    IReadOnlyList<ReplayCost> ObservedCosts,
    IReadOnlyList<ReplaySelection> Children);

public sealed record ReplayForce(
    string ForceEntryId, string CatalogueId,
    IReadOnlyList<ReplaySelection> Selections,
    IReadOnlyList<ReplayForce> ChildForces);

public sealed record ReplayRoster(
    string Name, string? GameSystemId,
    IReadOnlyList<ReplayCost> ObservedTotals,
    IReadOnlyList<string> BooksRevisions,
    IReadOnlyList<ReplayForce> Forces,
    IReadOnlyList<string> Unmapped);
