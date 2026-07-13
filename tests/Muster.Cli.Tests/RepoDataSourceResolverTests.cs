using BattleScribeSpec;
using BattleScribeSpec.Roster;
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

    [Fact]
    public void Create_resolver_drives_RosterRunner_which_calls_engine_SetupFromFiles_with_the_resolved_files()
    {
        // Exercises the real chain end-to-end: fixture YAML -> RepoDataSourceResolver ->
        // RosterRunner -> IRosterEngine.SetupFromFiles — rather than hand-mirroring
        // RosterRunner's internal file-collection logic (as the test above does).
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var cachedRepoDir = Path.Combine(dataDir, "github", "test-org", "test-repo", "main");
        Directory.CreateDirectory(cachedRepoDir);
        const string systemGstContent = "<gameSystem id=\"gs-1\" name=\"Test System\" />";
        File.WriteAllText(Path.Combine(cachedRepoDir, "system.gst"), systemGstContent);

        var spec = SpecLoader.LoadFromYaml("""
            id: fake-engine-datasource
            category: runner
            description: exercises RepoDataSourceResolver -> RosterRunner -> engine.SetupFromFiles
            setup:
              dataSource: "github:test-org/test-repo@main"
            steps:
              - expectedState: {}
            """);

        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, RepoDataSourceResolver.Create(dataDir), engineName: "wham");

        runner.Run(spec);

        Assert.NotNull(engine.ReceivedFiles);
        Assert.Contains(engine.ReceivedFiles, f => f.FileName == "system.gst" && f.Content == systemGstContent);
    }

    [Fact]
    public void IsPopulatedFor_returns_true_for_a_populated_github_cache_hit()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var cachedRepoDir = Path.Combine(dataDir, "github", "BSData", "wh40k-10e", "v10.6.0");
        Directory.CreateDirectory(cachedRepoDir);
        File.WriteAllText(Path.Combine(cachedRepoDir, "Warhammer 40,000.gst"), "<gameSystem />");

        Assert.True(RepoDataSourceResolver.IsPopulatedFor(dataDir, "github:BSData/wh40k-10e@v10.6.0"));
    }

    [Fact]
    public void IsPopulatedFor_returns_false_when_the_ref_does_not_match_a_populated_cache_dir()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var cachedRepoDir = Path.Combine(dataDir, "github", "BSData", "wh40k-10e", "v10.6.0");
        Directory.CreateDirectory(cachedRepoDir);
        File.WriteAllText(Path.Combine(cachedRepoDir, "Warhammer 40,000.gst"), "<gameSystem />");

        Assert.False(RepoDataSourceResolver.IsPopulatedFor(dataDir, "github:BSData/wh40k-10e@v10.7.0"));
    }

    [Fact]
    public void IsPopulatedFor_returns_true_for_an_existing_local_directory()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var localDir = Directory.CreateTempSubdirectory("muster-local").FullName;

        Assert.True(RepoDataSourceResolver.IsPopulatedFor(dataDir, $"local:{localDir}"));
    }

    [Fact]
    public void IsPopulatedFor_returns_false_for_a_missing_local_directory()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;
        var missingDir = Path.Combine(Path.GetTempPath(), $"muster-missing-{Guid.NewGuid():N}");

        Assert.False(RepoDataSourceResolver.IsPopulatedFor(dataDir, $"local:{missingDir}"));
    }

    [Fact]
    public void IsPopulatedFor_returns_false_for_a_garbage_dataSource_uri_without_throwing()
    {
        var dataDir = Directory.CreateTempSubdirectory("muster-datarepo").FullName;

        Assert.False(RepoDataSourceResolver.IsPopulatedFor(dataDir, "not-a-valid-uri"));
        Assert.False(RepoDataSourceResolver.IsPopulatedFor(dataDir, "ftp:some/unsupported/scheme"));
        Assert.False(RepoDataSourceResolver.IsPopulatedFor(dataDir, ""));
    }

    /// <summary>
    /// Minimal <see cref="IRosterEngine"/> fake, mirroring the pattern used by
    /// <c>RunnerAndProtocolRegressionTests.FakeEngine</c> in the wham battlescribe-spec
    /// TestKit's own regression tests. Only <see cref="SetupFromFiles"/> and the state
    /// getters matter for this test; every other action member either isn't reached
    /// (no action steps in the fixture) or would throw if it were.
    /// </summary>
    private sealed class FakeEngine : IRosterEngine
    {
        public List<(string FileName, string Content)>? ReceivedFiles { get; private set; }

        public IReadOnlyList<string> Setup(BattleScribeSpec.Protocol.ProtocolGameSystem gameSystem, BattleScribeSpec.Protocol.ProtocolCatalogue[] catalogues) =>
            throw new NotSupportedException("Not exercised: this test uses dataSource setup, not inline setup.");

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public void RemoveForce(string forceId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public ActionOutputs SelectEntry(string forceId, string entryId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public void DeselectSelection(string forceId, string selectionId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public void SetSelectionCount(string forceId, string selectionId, int count) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public ActionOutputs DuplicateForce(string forceId) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public void SetCostLimit(string costTypeId, decimal value) =>
            throw new NotSupportedException("Not exercised: this test's fixture has no action steps.");

        public RosterState GetRosterState() => new("roster", "gs", [], [], []);

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

        public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        {
            ReceivedFiles = [.. files];
            return [];
        }

        public void Dispose()
        {
        }
    }
}
