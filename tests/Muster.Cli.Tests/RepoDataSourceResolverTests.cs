using BattleScribeSpec;
using Muster.Cli.Fixtures;
using Xunit;

namespace Muster.Cli.Tests;

public class RepoDataSourceResolverTests
{
    [Fact]
    public void Create_resolves_local_dataSource_against_a_directory_under_the_data_repo()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var systemDir = Path.Combine(dataDir, "Warhammer 40,000");
        Directory.CreateDirectory(systemDir);
        var gstPath = Path.Combine(systemDir, "Warhammer 40,000.gst");
        var catPath = Path.Combine(systemDir, "Necrons.cat");
        File.WriteAllText(gstPath, "<gameSystem />");
        File.WriteAllText(catPath, "<catalogue />");

        var resolver = RepoDataSourceResolver.Create(dataDir);
        var resolvedDir = resolver.Resolve($"local:{systemDir}");

        Assert.Equal(systemDir, resolvedDir);
        var files = Directory.EnumerateFiles(resolvedDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Create_resolves_github_dataSource_from_a_pre_populated_data_repo_cache_without_network_access()
    {
        // The data repo directory is laid out exactly like DataSourceResolver's own
        // github cache: {dataDir}/github/{org}/{repo}/{ref}. Pre-populating it lets
        // fixtures use real-world "github:org/repo@ref" dataSource values while tests
        // stay hermetic (no git process is ever started, since the cache is already
        // "populated" from DataSourceResolver's point of view).
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var cachedRepoDir = Path.Combine(dataDir, "github", "BSData", "wh40k-10e", "v10.6.0");
        Directory.CreateDirectory(cachedRepoDir);
        var gstPath = Path.Combine(cachedRepoDir, "Warhammer 40,000.gst");
        var catPath = Path.Combine(cachedRepoDir, "Necrons.cat");
        File.WriteAllText(gstPath, "<gameSystem />");
        File.WriteAllText(catPath, "<catalogue />");

        var resolver = RepoDataSourceResolver.Create(dataDir);
        var resolvedDir = resolver.Resolve("github:BSData/wh40k-10e@v10.6.0");

        Assert.Equal(cachedRepoDir, resolvedDir);
    }

    [Fact]
    public void Create_returns_a_DataSourceResolver_usable_by_RosterRunner()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var cachedRepoDir = Path.Combine(dataDir, "github", "TestOrg", "TestRepo", "latest");
        Directory.CreateDirectory(cachedRepoDir);
        File.WriteAllText(Path.Combine(cachedRepoDir, "System.gst"), "<gameSystem />");
        File.WriteAllText(Path.Combine(cachedRepoDir, "Catalogue.cat"), "<catalogue />");

        DataSourceResolver resolver = RepoDataSourceResolver.Create(dataDir);
        var resolvedDir = resolver.Resolve("github:TestOrg/TestRepo");

        var files = Directory.EnumerateFiles(resolvedDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            .Select(f => (FileName: Path.GetFileName(f), Content: File.ReadAllText(f)))
            .ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.FileName == "System.gst" && f.Content == "<gameSystem />");
        Assert.Contains(files, f => f.FileName == "Catalogue.cat" && f.Content == "<catalogue />");
    }
}
