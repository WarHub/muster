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
    public static DataSourceResolver Create(string dataDir) => new(cacheDir: dataDir);
}
