using System.CommandLine;
using System.Diagnostics;
using BattleScribeSpec;
using BattleScribeSpec.Roster;
using Muster.Cli.Fixtures;
using Muster.Cli.Reporting;
using WarHub.ArmouryModel.RosterEngine.Spec;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster test --data &lt;dir&gt; --fixtures &lt;dir&gt;</c> — evaluates golden-roster
/// fixtures (battlescribe-spec roster DSL YAML files) against a wargame data repo,
/// using wham's roster engine.
/// </summary>
public static class TestCommand
{
    private const string EngineName = "wham";

    public static Command Create()
    {
        var dataOption = new Option<DirectoryInfo>("--data")
        {
            Description = "Data repo root.",
            Required = true,
        };
        var fixturesOption = new Option<DirectoryInfo>("--fixtures")
        {
            Description = "Golden fixtures directory.",
            Required = true,
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "summary|json|github-actions",
            DefaultValueFactory = _ => "summary",
        };
        var reportOption = new Option<FileInfo?>("--report")
        {
            Description = "Write JSON run report to path.",
        };

        var command = new Command("test", "Evaluate golden roster fixtures against the data repo.");
        command.Options.Add(dataOption);
        command.Options.Add(fixturesOption);
        command.Options.Add(outputOption);
        command.Options.Add(reportOption);
        command.SetAction(parse => Run(
            parse.GetValue(dataOption)!,
            parse.GetValue(fixturesOption)!,
            parse.GetValue(outputOption)!,
            parse.GetValue(reportOption)));

        return command;
    }

    internal static int Run(DirectoryInfo data, DirectoryInfo fixtures, string output, FileInfo? report)
    {
        try
        {
            var runReport = RunFixtures(data.FullName, fixtures.FullName);
            RunReport.Write(runReport, output, Console.Out);
            if (report is not null)
            {
                File.WriteAllText(report.FullName, RunReport.ToJson(runReport));
            }

            return runReport.Failed > 0 ? 1 : runReport.Inconclusive > 0 ? 2 : 0;
        }
        catch (HarnessInconclusiveException ex)
        {
            return Inconclusive(ex.Message);
        }
        catch (Exception ex)
        {
            return Inconclusive($"harness error: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs every fixture under <paramref name="fixturesDir"/> against <paramref name="dataDir"/>
    /// and returns the aggregate <see cref="RunReport"/>. Shared by <see cref="TestCommand"/> and
    /// <c>DiffCommand</c> so both commands evaluate fixtures identically.
    /// </summary>
    /// <exception cref="HarnessInconclusiveException">
    /// Thrown for harness-level (not per-fixture) problems: a missing data or fixtures
    /// directory, or a fixtures directory with no <c>*.yaml</c> fixtures in it.
    /// </exception>
    internal static RunReport RunFixtures(string dataDir, string fixturesDir)
    {
        if (!Directory.Exists(dataDir))
        {
            throw new HarnessInconclusiveException($"data directory not found: {dataDir}");
        }

        if (!Directory.Exists(fixturesDir))
        {
            throw new HarnessInconclusiveException($"fixtures directory not found: {fixturesDir}");
        }

        var fixturePaths = Directory
            .EnumerateFiles(fixturesDir, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (fixturePaths.Count == 0)
        {
            throw new HarnessInconclusiveException("no fixtures found");
        }

        var resolver = RepoDataSourceResolver.Create(dataDir);
        var results = new List<FixtureResult>(fixturePaths.Count);
        foreach (var path in fixturePaths)
        {
            results.Add(RunFixture(path, dataDir, resolver));
        }

        return RunReport.Create(EngineName, dataDir, results);
    }

    private static FixtureResult RunFixture(string path, string dataDir, DataSourceResolver resolver)
    {
        var sw = Stopwatch.StartNew();

        SpecFile spec;
        try
        {
            // SpecLoader.Load validates eagerly — malformed fixtures throw here.
            spec = SpecLoader.Load(path);
        }
        catch (Exception ex)
        {
            var fallbackId = Path.GetFileNameWithoutExtension(path);
            return new FixtureResult(
                fallbackId, path, Passed: false,
                [$"fixture parse error: {ex.Message}"], sw.ElapsedMilliseconds, Inconclusive: true);
        }

        // Hermeticity gate: never let RosterRunner->DataSourceResolver.Resolve fall through
        // to a live git clone. Only run fixtures whose data source is already populated
        // locally under `dataDir`.
        if (spec.Setup.DataSource is { Length: > 0 } dataSource
            && !RepoDataSourceResolver.IsPopulatedFor(dataDir, dataSource))
        {
            return new FixtureResult(
                spec.Id, path, Passed: false,
                [$"data source not populated locally — refusing non-hermetic resolution: {dataSource}"],
                sw.ElapsedMilliseconds, Inconclusive: true);
        }

        try
        {
            using IRosterEngine engine = new SpecRosterEngineAdapter();
            var runner = new RosterRunner(engine, resolver, engineName: EngineName);
            var result = runner.Run(spec);
            if (result.HarnessError is { } harnessError)
            {
                // Engine/harness crashed inside the runner (setup or step execution):
                // classify as inconclusive, not a genuine assertion failure.
                return new FixtureResult(
                    spec.Id, path, Passed: false, [harnessError], sw.ElapsedMilliseconds, Inconclusive: true);
            }

            return new FixtureResult(
                spec.Id, path, result.Passed, [.. result.Failures], sw.ElapsedMilliseconds, Inconclusive: false);
        }
        catch (Exception ex) // engine crash on THIS fixture: inconclusive, not failed
        {
            return new FixtureResult(
                spec.Id, path, Passed: false,
                [$"engine crash: {ex.GetType().Name}: {ex.Message}"], sw.ElapsedMilliseconds, Inconclusive: true);
        }
    }

    private static int Inconclusive(string message)
    {
        Console.Error.WriteLine($"::warning::muster inconclusive: {message}");
        return 2;
    }
}

/// <summary>
/// Signals a harness-level problem with a fixture run (missing data/fixtures directory,
/// no fixtures found) as opposed to a per-fixture pass/fail/inconclusive result.
/// </summary>
internal sealed class HarnessInconclusiveException(string message) : Exception(message);
