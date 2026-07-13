namespace Muster.Cli.Tests;

/// <summary>
/// Builds throwaway data-repo/fixtures trees for e2e tests of the muster CLI commands.
/// </summary>
internal static class TestRepoFactory
{
    /// <summary>
    /// Creates a temp data repo root (laid out to mirror <c>DataSourceResolver</c>'s own
    /// github cache structure: <c>{dataDir}/github/{org}/{repo}/{ref}/*.gst,*.cat</c> — see
    /// <see cref="Muster.Cli.Fixtures.RepoDataSourceResolver"/>) plus a fixtures dir with a
    /// single green golden-roster fixture pinning the test unit's cost at 20 points.
    /// </summary>
    public static (string DataDir, string FixturesDir) CreateTestRepo()
    {
        var root = Directory.CreateTempSubdirectory("muster-e2e").FullName;
        var dataRoot = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var fixtures = Directory.CreateDirectory(Path.Combine(root, "tests", "rosters")).FullName;

        // Data repo laid out to mirror DataSourceResolver's own github cache structure:
        // {dataDir}/github/{org}/{repo}/{ref}/*.gst,*.cat — see RepoDataSourceResolver.
        var cachedRepoDir = Directory.CreateDirectory(
            Path.Combine(dataRoot, "github", "muster-e2e", "test-data", "main")).FullName;

        File.WriteAllText(Path.Combine(cachedRepoDir, "system.gst"), """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <gameSystem id="gs-test" name="Test System" revision="1" battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/gameSystemSchema">
              <costTypes>
                <costType id="ct-pts" name="pts" defaultCostLimit="-1.0" hidden="false"/>
              </costTypes>
              <forceEntries>
                <forceEntry id="fe-army" name="Army" hidden="false"/>
              </forceEntries>
            </gameSystem>
            """);
        File.WriteAllText(Path.Combine(cachedRepoDir, "catalogue.cat"), """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <catalogue id="cat-test" name="Test Catalogue" revision="1" battleScribeVersion="2.03" gameSystemId="gs-test" gameSystemRevision="1" xmlns="http://www.battlescribe.net/schema/catalogueSchema">
              <selectionEntries>
                <selectionEntry id="se-unit" name="Test Unit" hidden="false" type="unit">
                  <costs>
                    <cost name="pts" typeId="ct-pts" value="20.0"/>
                  </costs>
                </selectionEntry>
              </selectionEntries>
            </catalogue>
            """);

        File.WriteAllText(Path.Combine(fixtures, "unit-costs-20.yaml"), """
            id: unit-costs-20
            category: golden
            description: Test Unit costs 20 points
            setup:
              dataSource: "github:muster-e2e/test-data@main"
            steps:
              - action: addForce
                id: add-army
                forceEntryId: fe-army
                catalogueId: cat-test
              - action: selectEntry
                forceId: ${{ steps.add-army.forceId }}
                entryId: se-unit
              - expectedState:
                  costs:
                    - typeId: ct-pts
                      value: 20
            """);

        return (dataRoot, fixtures);
    }

    /// <summary>
    /// Deep-copies <paramref name="sourceDir"/> to a new temp directory and replaces every
    /// literal occurrence of <paramref name="find"/> with <paramref name="replace"/> across
    /// all copied files. Used to derive a "head" data tree that differs from "base" by one
    /// data value (e.g. a cost), without mutating the original.
    /// </summary>
    public static string CopyWithReplacement(string sourceDir, string find, string replace)
    {
        var destRoot = Directory.CreateTempSubdirectory("muster-e2e-copy").FullName;
        CopyDirectory(sourceDir, destRoot);

        foreach (var file in Directory.EnumerateFiles(destRoot, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            if (content.Contains(find, StringComparison.Ordinal))
            {
                File.WriteAllText(file, content.Replace(find, replace, StringComparison.Ordinal));
            }
        }

        return destRoot;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
        }
    }
}
