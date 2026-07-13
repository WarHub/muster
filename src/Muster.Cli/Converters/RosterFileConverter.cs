using System.IO.Compression;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;
using WarHub.ArmouryModel.Workspaces.BattleScribe;

namespace Muster.Cli.Converters;

/// <summary>
/// Converts a BattleScribe .ros/.rosz roster file into a ReplayRoster.
/// SelectionNode.EntryId is already the composite "linkId::targetId" form the
/// engine's by-id selection expects, so ids pass through verbatim.
/// </summary>
/// <remarks>
/// .rosz handling is done here with a plain <see cref="ZipArchive"/> rather than
/// wham's <c>Stream.LoadSourceAuto</c> (Workspaces.BattleScribe/XmlFileExtensions.cs).
/// That helper dispatches zipped vs. plain XML via
/// <c>XmlDocumentKind.IsXmlZipped()</c>, which resolves the extension through
/// <c>ExtensionsByKinds[kind][0]</c> — always the *unzipped* extension for the kind,
/// regardless of what the actual file/entry name was. In practice <c>IsXmlZipped()</c>
/// is always false, so <c>LoadSourceAuto("x.rosz")</c> silently takes the plain-XML
/// path and throws "Data at the root level is invalid" on real .rosz input (confirmed
/// by a throwaway repro test against the submodule as vendored). We only use
/// <see cref="XmlFileExtensions.RosterZipped"/> from that assembly (a plain constant,
/// unaffected by the bug) and do the exactly-one-entry zip rule ourselves.
/// </remarks>
public static class RosterFileConverter
{
    private const int MaxSelections = 5_000;

    public static ReplayRoster Convert(Stream stream, string fileName)
    {
        RosterNode roster;
        try
        {
            roster = fileName.EndsWith(XmlFileExtensions.RosterZipped, StringComparison.OrdinalIgnoreCase)
                ? LoadZipped(stream)
                : BattleScribeXml.LoadRoster(stream)
                    ?? throw new FormatException("file is not a BattleScribe roster");
        }
        catch (Exception e) when (e is not FormatException)
        {
            throw new FormatException($"could not read roster file: {e.Message}");
        }

        var count = 0;
        var unmapped = new List<string>();
        var forces = ConvertForces(roster.Forces, ref count, unmapped);
        if (forces.Count == 0)
            throw new FormatException("roster has no forces");

        return new(
            Name: roster.Name ?? "unnamed roster",
            GameSystemId: roster.GameSystemId,
            ObservedTotals: [.. roster.Costs.Select(c => new ReplayCost(c.Name ?? "", c.TypeId ?? "", c.Value))],
            BooksRevisions: [],
            Forces: forces,
            Unmapped: unmapped);
    }

    private static RosterNode LoadZipped(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != 1)
        {
            throw new FormatException(
                $".rosz archive must contain exactly one entry, found {archive.Entries.Count}");
        }
        using var entryStream = archive.Entries[0].Open();
        return BattleScribeXml.LoadRoster(entryStream)
            ?? throw new FormatException("archive entry is not a BattleScribe roster");
    }

    private static ReplayForce ConvertForce(ForceNode force, ref int count, List<string> unmapped) => new(
        ForceEntryId: force.EntryId ?? throw new FormatException($"force '{force.Name}' has no entryId"),
        CatalogueId: force.CatalogueId ?? "",
        Selections: ConvertSelections(force.Selections, ref count, unmapped),
        ChildForces: ConvertForces(force.Forces, ref count, unmapped));

    private static List<ReplayForce> ConvertForces(IEnumerable<ForceNode> forces, ref int count, List<string> unmapped)
    {
        var result = new List<ReplayForce>();
        foreach (var f in forces) result.Add(ConvertForce(f, ref count, unmapped));
        return result;
    }

    private static List<ReplaySelection> ConvertSelections(IEnumerable<SelectionNode> selections, ref int count, List<string> unmapped)
    {
        var result = new List<ReplaySelection>();
        foreach (var s in selections)
        {
            if (++count > MaxSelections)
                throw new FormatException($"roster too large (over {MaxSelections} selections)");
            if (s.EntryId is null)
            {
                // No way to replay a selection without an entryId — surface as structural
                // drift rather than silently dropping it from the converted roster.
                unmapped.Add($"selection '{s.Name}' (id={s.Id}) has no entryId — cannot be replayed");
                continue;
            }
            result.Add(new(
                EntryId: s.EntryId,
                Count: s.Number,
                CustomName: s.CustomName,
                ObservedCosts: [.. s.Costs.Select(c => new ReplayCost(c.Name ?? "", c.TypeId ?? "", c.Value))],
                Children: ConvertSelections(s.Selections, ref count, unmapped)));
        }
        return result;
    }
}
