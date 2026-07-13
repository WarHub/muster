using BattleScribeSpec;
using BattleScribeSpec.Roster;

namespace Muster.Cli.Fixtures;

/// <summary>
/// Loads golden-roster fixtures — battlescribe-spec roster DSL YAML files —
/// from a directory on disk.
/// </summary>
public static class FixtureLoader
{
    /// <summary>
    /// Discover and load every "*.yaml" spec file under <paramref name="fixturesDir"/>
    /// (recursively), in ordinal path order.
    /// </summary>
    public static IReadOnlyList<(string Path, SpecFile Spec)> LoadDirectory(string fixturesDir) =>
        Directory.EnumerateFiles(fixturesDir, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, SpecLoader.Load(path)))
            .ToList();
}
