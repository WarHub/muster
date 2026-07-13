using System.CommandLine;
using Muster.Cli.Engines;
using Muster.Cli.Reports;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster promote --issue-body &lt;file&gt; --comments &lt;file&gt; --data &lt;root&gt;
/// --issue-number &lt;n&gt; [--engines …] [--governing …] [--out &lt;dir&gt;]</c> — extracts the
/// newest executable-spec snapshot from a <c>muster report</c> issue's comments, re-runs it
/// against current data to re-pin its assertions, and writes the result as a golden fixture
/// under <c>tests/rosters</c>.
/// </summary>
public static class PromoteCommand
{
    public static Command Create()
    {
        var issueBodyOption = new Option<FileInfo>("--issue-body")
        {
            Description = "Path to a file containing the GitHub issue body text.",
            Required = true,
        };
        var commentsOption = new Option<FileInfo>("--comments")
        {
            Description = "Path to a file containing `gh api /repos/{o}/{r}/issues/{n}/comments` JSON output.",
            Required = true,
        };
        var dataOption = new Option<DirectoryInfo>("--data")
        {
            Description = "Data repo root.",
            Required = true,
        };
        var issueNumberOption = new Option<int>("--issue-number")
        {
            Description = "GitHub issue number — used to slug the fixture file (report-issue-<n>.yaml).",
            Required = true,
        };
        var enginesOption = new Option<string[]>("--engines")
        {
            Description = "Engines to run: 'wham' (builtin), 'name=dotnet:path.dll', 'name=docker:image', 'name=exe args'. Default: wham.",
            AllowMultipleArgumentsPerToken = true,
        };
        var governingOption = new Option<string[]>("--governing")
        {
            Description = "Governing-engine precedence (first match that's available governs). Default: newrecruit battlescribe wham.",
            AllowMultipleArgumentsPerToken = true,
        };
        var outOption = new Option<DirectoryInfo>("--out")
        {
            Description = "Directory to write the promoted fixture into.",
            DefaultValueFactory = _ => new DirectoryInfo(Path.Combine("tests", "rosters")),
        };

        var command = new Command("promote", "Promote a bug report's executable spec snapshot into a pinned golden fixture.");
        command.Options.Add(issueBodyOption);
        command.Options.Add(commentsOption);
        command.Options.Add(dataOption);
        command.Options.Add(issueNumberOption);
        command.Options.Add(enginesOption);
        command.Options.Add(governingOption);
        command.Options.Add(outOption);
        command.SetAction((parse, ct) => Run(
            parse.GetValue(issueBodyOption)!,
            parse.GetValue(commentsOption)!,
            parse.GetValue(dataOption)!,
            parse.GetValue(issueNumberOption),
            parse.GetValue(enginesOption) ?? [],
            parse.GetValue(governingOption) ?? [],
            parse.GetValue(outOption)!,
            ct));

        return command;
    }

    internal static async Task<int> Run(
        FileInfo issueBody, FileInfo comments, DirectoryInfo data, int issueNumber,
        string[] engines, string[] governing, DirectoryInfo outDir, CancellationToken ct = default)
    {
        try
        {
            if (!issueBody.Exists)
            {
                Console.Error.WriteLine($"issue body file not found: {issueBody.FullName}");
                return 2;
            }

            if (!comments.Exists)
            {
                Console.Error.WriteLine($"comments file not found: {comments.FullName}");
                return 2;
            }

            if (!data.Exists)
            {
                Console.Error.WriteLine($"data directory not found: {data.FullName}");
                return 2;
            }

            var commentsJson = await File.ReadAllTextAsync(comments.FullName, ct);
            var snapshotYaml = SnapshotExtractor.ExtractLatest(commentsJson);
            if (snapshotYaml is null)
            {
                Console.Error.WriteLine("no executable spec snapshot found in issue comments");
                return 2;
            }

            var engineSpec = ResolveGoverningEngine(engines, governing);
            if (engineSpec is null)
            {
                Console.Error.WriteLine("no governing engine is available to replay the snapshot");
                return 2;
            }

            outDir.Create();
            var (slug, path) = ResolveSlug(outDir.FullName, issueNumber);

            var rePinned = SpecRePinner.RePin(snapshotYaml, data.FullName, engineSpec, slug);

            await File.WriteAllTextAsync(path, rePinned, ct);
            Console.Out.WriteLine(path);
            return 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>
    /// Resolves the single governing engine among those available: parses
    /// <paramref name="engines"/> (default: builtin wham), filters to the ones actually
    /// available in this environment, and picks the first match in
    /// <paramref name="governing"/> precedence (default: <see cref="EngineRegistry.DefaultGoverning"/>).
    /// Promote runs exactly one engine — the one whose evaluation would govern a
    /// <c>muster report</c>/<c>muster test</c> verdict — never a multi-engine matrix.
    /// </summary>
    private static EngineSpec? ResolveGoverningEngine(string[] engines, string[] governing)
    {
        var available = EngineRegistry.ParseAll(engines).Where(EngineRegistry.IsAvailable).ToList();
        var precedence = governing.Length > 0 ? governing : EngineRegistry.DefaultGoverning;
        var governingName = EngineRegistry.ResolveGoverning(precedence, [.. available.Select(s => s.Name)]);
        return available.FirstOrDefault(s => s.Name == governingName);
    }

    private static (string Slug, string Path) ResolveSlug(string outDir, int issueNumber)
    {
        var baseSlug = $"report-issue-{issueNumber}";
        var slug = baseSlug;
        var path = Path.Combine(outDir, $"{slug}.yaml");
        for (var n = 2; File.Exists(path); n++)
        {
            slug = $"{baseSlug}-{n}";
            path = Path.Combine(outDir, $"{slug}.yaml");
        }

        return (slug, path);
    }
}
