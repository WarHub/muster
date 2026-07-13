using BattleScribeSpec;

namespace Muster.Cli.Fixtures;

/// <summary>
/// Builds a <see cref="DataSourceResolver"/> that resolves a fixture's
/// <c>setup.dataSource</c> value against a local data repo directory.
/// </summary>
/// <remarks>
/// <see cref="DataSourceResolver"/> is a sealed TestKit type whose constructor
/// takes a <c>cacheDir</c>. For <c>"local:{path}"</c> data sources it resolves
/// the path as-is (cacheDir is unused). For <c>"github:{org}/{repo}[@{ref}]"</c>
/// data sources it first checks whether <c>{cacheDir}/github/{org}/{repo}/{ref-or-latest}</c>
/// is already populated and, if so, uses it directly instead of cloning.
/// Passing the data repo directory as <c>cacheDir</c> therefore lets fixtures use
/// real-world <c>dataSource</c> values (as authored in production rosters) while
/// resolving them against pre-fetched files in the data repo — no network access,
/// and no bespoke name-to-file mapping logic required.
/// </remarks>
public static class RepoDataSourceResolver
{
    /// <summary>
    /// Creates a <see cref="DataSourceResolver"/> rooted at <paramref name="dataDir"/>.
    /// </summary>
    /// <remarks>
    /// <b>Hermeticity warning:</b> an unmatched <c>github:</c> ref triggers a live
    /// <c>git clone</c> in the underlying TestKit resolver — callers MUST gate calls
    /// that use the returned resolver with <see cref="IsPopulatedFor"/> to stay hermetic
    /// (e.g. in CI, where network access should never be required).
    /// </remarks>
    public static DataSourceResolver Create(string dataDir) => new(cacheDir: dataDir);

    /// <summary>
    /// Returns whether <paramref name="dataSource"/> resolves to an existing, populated
    /// directory under <paramref name="dataDir"/> — i.e. whether a resolver returned by
    /// <see cref="Create"/> can resolve it without any network access.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>DataSourceResolver</c>'s own cache-hit check: for
    /// <c>"github:{org}/{repo}[@{ref}]"</c> data sources, checks whether
    /// <c>{dataDir}/github/{org}/{repo}/{ref-or-latest}</c> exists and has content beyond a
    /// <c>.git</c> directory. For <c>"local:{path}"</c> data sources, checks whether the
    /// literal path exists. Unknown schemes and unparsable data source strings return
    /// <see langword="false"/> rather than throwing.
    /// </remarks>
    public static bool IsPopulatedFor(string dataDir, string dataSource)
    {
        if (!DataSourceUri.TryParse(dataSource, out var uri) || uri is null)
        {
            return false;
        }

        return uri.Provider switch
        {
            "github" => IsPopulatedGithubCache(dataDir, uri),
            "local" => Directory.Exists(uri.Repo),
            _ => false,
        };
    }

    private static bool IsPopulatedGithubCache(string dataDir, DataSourceUri uri)
    {
        var cachePath = Path.Combine(
            [dataDir, .. uri.CacheKey.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);

        return Directory.Exists(cachePath)
            && Directory.EnumerateFileSystemEntries(cachePath)
                .Any(e => !Path.GetFileName(e).Equals(".git", StringComparison.OrdinalIgnoreCase));
    }
}
