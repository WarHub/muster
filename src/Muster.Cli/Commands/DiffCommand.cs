using System.CommandLine;
using Muster.Cli.Reporting;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster diff --base &lt;dir&gt; --head &lt;dir&gt; --fixtures &lt;dir&gt;</c> — runs the same golden-roster
/// fixtures against two data trees, for one or more roster engines, and reports the blast
/// radius: which fixtures changed outcome between base and head.
/// </summary>
public static class DiffCommand
{
    public static Command Create()
    {
        var baseOption = new Option<DirectoryInfo>("--base")
        {
            Description = "Base data repo root.",
            Required = true,
        };
        var headOption = new Option<DirectoryInfo>("--head")
        {
            Description = "Head data repo root.",
            Required = true,
        };
        var fixturesOption = new Option<DirectoryInfo>("--fixtures")
        {
            Description = "Golden fixtures directory (run against both base and head).",
            Required = true,
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "markdown|json",
            DefaultValueFactory = _ => "markdown",
        };
        var failOnBrokeOption = new Option<bool>("--fail-on-broke")
        {
            Description = "Exit 1 when the governing engine classifies any fixture as broke or verdict-changed.",
            DefaultValueFactory = _ => false,
        };
        var enginesOption = new Option<string[]>("--engines")
        {
            Description = "Engines to run: 'wham' (builtin), 'name=dotnet:path.dll', 'name=docker:image', 'name=exe args'. Default: wham.",
            AllowMultipleArgumentsPerToken = true,
        };
        var governingOption = new Option<string[]>("--governing")
        {
            Description = "Governing-engine precedence (first match that ran governs). Default: newrecruit battlescribe wham.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("diff", "Report blast radius of fixture outcomes between two data trees.");
        command.Options.Add(baseOption);
        command.Options.Add(headOption);
        command.Options.Add(fixturesOption);
        command.Options.Add(outputOption);
        command.Options.Add(failOnBrokeOption);
        command.Options.Add(enginesOption);
        command.Options.Add(governingOption);
        command.SetAction(parse => Run(
            parse.GetValue(baseOption)!,
            parse.GetValue(headOption)!,
            parse.GetValue(fixturesOption)!,
            parse.GetValue(outputOption)!,
            parse.GetValue(failOnBrokeOption),
            parse.GetValue(enginesOption) ?? [],
            parse.GetValue(governingOption) ?? []));

        return command;
    }

    internal static int Run(
        DirectoryInfo baseDir, DirectoryInfo headDir, DirectoryInfo fixtures, string output,
        bool failOnBroke, IReadOnlyList<string> engines, IReadOnlyList<string> governing)
    {
        MultiRunReport baseRuns;
        MultiRunReport headRuns;
        try
        {
            baseRuns = MultiRunReport.Run(baseDir.FullName, fixtures.FullName, engines, governing);
            headRuns = MultiRunReport.Run(headDir.FullName, fixtures.FullName, engines, governing);
        }
        catch (HarnessInconclusiveException ex)
        {
            return Inconclusive(ex.Message);
        }
        catch (Exception ex)
        {
            return Inconclusive($"harness error: {ex.Message}");
        }

        // Diff reports what changed, it doesn't judge — exit 0 even when fixtures broke,
        // as long as both runs completed at the harness level, unless --fail-on-broke gates
        // on the governing engine's own rows.
        var report = BlastRadius.ClassifyMulti(baseRuns, headRuns);
        BlastRadius.WriteMulti(report, output, Console.Out);

        if (failOnBroke && report.Governing is { } gov)
        {
            var governingRows = report.Diffs.FirstOrDefault(d => d.Engine == gov)?.Rows ?? [];
            if (governingRows.Any(r => r.Classification is "broke" or "verdict-changed"))
            {
                return 1;
            }
        }

        return 0;
    }

    private static int Inconclusive(string message)
    {
        Console.Error.WriteLine($"::warning::muster inconclusive: {message}");
        return 2;
    }
}
