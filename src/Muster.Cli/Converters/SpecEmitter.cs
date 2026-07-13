using System.Globalization;
using System.Text;

namespace Muster.Cli.Converters;

/// <summary>
/// Emits fixture-DSL YAML from a ReplayRoster. TestKit has no SpecFile→YAML
/// serializer, so YAML is emitted as text; every caller path is validated by
/// round-tripping through SpecLoader.LoadFromYaml in tests.
/// Step ids: force-1, force-2, … / sel-1, sel-2, … in document order.
/// All string scalars are double-quoted — sidesteps YAML coercion traps.
/// Step-reference expressions (${{ steps.x.y }}) are quoted too: YamlDotNet's
/// double-quoted scalar parsing yields the literal text unchanged (no special
/// handling of "${{" / "}}"), and expression resolution happens later, at run
/// time, against the deserialized string — so quoting is transparent to it.
/// </summary>
public static class SpecEmitter
{
    public static string Emit(ReplayRoster roster, string specId, string dataSource, bool pinObserved)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"id: {Quote(specId)}");
        sb.AppendLine("category: report");
        sb.AppendLine($"description: {Quote($"Converted from bug report roster '{roster.Name}'")}");
        sb.AppendLine("setup:");
        sb.AppendLine($"  dataSource: {Quote(dataSource)}");
        sb.AppendLine();
        sb.AppendLine("steps:");

        var forceIndex = 0;
        var selIndex = 0;
        foreach (var force in roster.Forces)
            EmitForce(sb, force, parentForceStep: null, ref forceIndex, ref selIndex);

        if (pinObserved && roster.ObservedTotals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  # observed values from the report — PASS means the reported state reproduces");
            sb.AppendLine("  - expectedState:");
            sb.AppendLine("      costs:");
            foreach (var cost in roster.ObservedTotals)
            {
                sb.AppendLine($"        - typeId: {Quote(cost.TypeId)}");
                sb.AppendLine($"          value: {cost.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (pinObserved && HasAnyObservedCosts(roster))
        {
            sb.AppendLine();
            sb.AppendLine("  # per-selection costs observed but not pinned (comparer is order-sensitive)");
        }

        return sb.ToString();
    }

    // Step 4 decision (see RosterRunner.AssertSelections in
    // BattleScribeSpec.TestKit/Roster/RosterRunner.cs ~line 691-808): expected vs.
    // actual selections are matched by *position* (expected[si] against actual[si]),
    // not by name or entryId — costs within a matched pair are then looked up by
    // TypeId/Name, but getting to that pair at all depends on index alignment. If the
    // engine auto-adds/reorders selections (e.g. default child selections), a
    // per-selection cost pin emitted at the observed index would silently compare
    // against the wrong selection instead of failing loudly. So per-selection
    // observed costs are surfaced only as a YAML comment, never as expectedState
    // pins; only the roster-level totals (order-independent) are pinned.
    private static bool HasAnyObservedCosts(ReplayRoster roster) => roster.Forces.Any(HasAnyObservedCosts);

    private static bool HasAnyObservedCosts(ReplayForce force) =>
        force.Selections.Any(HasAnyObservedCosts) || force.ChildForces.Any(HasAnyObservedCosts);

    private static bool HasAnyObservedCosts(ReplaySelection sel) =>
        sel.ObservedCosts.Count > 0 || sel.Children.Any(HasAnyObservedCosts);

    private static void EmitForce(StringBuilder sb, ReplayForce force, string? parentForceStep, ref int forceIndex, ref int selIndex)
    {
        forceIndex++;
        var stepId = $"force-{forceIndex}";
        if (parentForceStep is null)
        {
            sb.AppendLine("  - action: addForce");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceEntryId: {Quote(force.ForceEntryId)}");
            sb.AppendLine($"    catalogueId: {Quote(force.CatalogueId)}");
        }
        else
        {
            sb.AppendLine("  - action: addChildForce");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {Quote($"${{{{ steps.{parentForceStep}.forceId }}}}")}");
            sb.AppendLine($"    forceEntryId: {Quote(force.ForceEntryId)}");
            sb.AppendLine($"    catalogueId: {Quote(force.CatalogueId)}");
        }

        foreach (var sel in force.Selections)
            EmitSelection(sb, sel, forceStep: stepId, parentSelStep: null, ref selIndex);

        foreach (var child in force.ChildForces)
            EmitForce(sb, child, stepId, ref forceIndex, ref selIndex);
    }

    private static void EmitSelection(StringBuilder sb, ReplaySelection sel, string forceStep, string? parentSelStep, ref int selIndex)
    {
        selIndex++;
        var stepId = $"sel-{selIndex}";
        var forceRef = Quote($"${{{{ steps.{forceStep}.forceId }}}}");
        if (parentSelStep is null)
        {
            sb.AppendLine("  - action: selectEntry");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    entryId: {Quote(sel.EntryId)}");
        }
        else
        {
            sb.AppendLine("  - action: selectChildEntry");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{parentSelStep}.selectionId }}}}")}");
            sb.AppendLine($"    entryId: {Quote(sel.EntryId)}");
        }

        if (sel.Count != 1)
        {
            sb.AppendLine("  - action: setSelectionCount");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{stepId}.selectionId }}}}")}");
            sb.AppendLine($"    count: {sel.Count}");
        }

        if (!string.IsNullOrEmpty(sel.CustomName))
        {
            sb.AppendLine("  - action: setCustomization");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{stepId}.selectionId }}}}")}");
            sb.AppendLine($"    customName: {Quote(sel.CustomName!)}");
        }

        foreach (var child in sel.Children)
            EmitSelection(sb, child, forceStep, stepId, ref selIndex);
    }

    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var sb = new StringBuilder(escaped.Length + 2);
        sb.Append('"');
        foreach (var c in escaped)
        {
            switch (c)
            {
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
