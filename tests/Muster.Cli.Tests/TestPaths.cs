namespace Muster.Cli.Tests;

/// <summary>
/// Shared filesystem-path lookups for e2e tests that need to spawn built artifacts
/// (e.g. the out-of-proc <c>Muster.TestAdapter</c>) as subprocesses.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Path to the built <c>Muster.TestAdapter.dll</c>, resolved by walking up from
    /// <see cref="AppContext.BaseDirectory"/> to the repo root (identified by
    /// <c>Muster.slnx</c>) and back down into <c>artifacts/bin</c>.
    /// </summary>
    public static string TestAdapterDll
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Muster.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException(
                    $"could not locate Muster.slnx above {AppContext.BaseDirectory}");
            }

            return Path.Combine(dir.FullName, "artifacts", "bin", "Muster.TestAdapter", "debug", "Muster.TestAdapter.dll");
        }
    }
}
