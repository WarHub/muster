using System.CommandLine;
using Muster.Cli.Reporting;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster diff --base &lt;dir&gt; --head &lt;dir&gt; --fixtures &lt;dir&gt;</c> — runs the same golden-roster
/// fixtures against two data trees and reports the blast radius: which fixtures changed
/// outcome between base and head.
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

        var command = new Command("diff", "Report blast radius of fixture outcomes between two data trees.");
        command.Options.Add(baseOption);
        command.Options.Add(headOption);
        command.Options.Add(fixturesOption);
        command.Options.Add(outputOption);
        command.SetAction(parse => Run(
            parse.GetValue(baseOption)!,
            parse.GetValue(headOption)!,
            parse.GetValue(fixturesOption)!,
            parse.GetValue(outputOption)!));

        return command;
    }

    internal static int Run(DirectoryInfo baseDir, DirectoryInfo headDir, DirectoryInfo fixtures, string output)
    {
        RunReport baseRun;
        RunReport headRun;
        try
        {
            baseRun = TestCommand.RunFixtures(baseDir.FullName, fixtures.FullName);
            headRun = TestCommand.RunFixtures(headDir.FullName, fixtures.FullName);
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
        // as long as both runs completed at the harness level.
        var rows = BlastRadius.Classify(baseRun, headRun);
        BlastRadius.Write(baseRun, headRun, rows, output, Console.Out);
        return 0;
    }

    private static int Inconclusive(string message)
    {
        Console.Error.WriteLine($"::warning::muster inconclusive: {message}");
        return 2;
    }
}
