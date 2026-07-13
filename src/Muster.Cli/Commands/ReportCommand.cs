using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec;
using Muster.Cli.Converters;
using Muster.Cli.NewRecruit;
using Muster.Cli.Reporting;
using Muster.Cli.Reports;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster report --issue-body &lt;file&gt; --data &lt;root&gt; [--engines …] [--governing …]
/// [--data-source &lt;uri&gt;] [--out-dir &lt;dir&gt;]</c> — turns a GitHub issue body (New Recruit
/// share link, roster attachment, or inline fixture-DSL YAML) into a verdict, labels, and a
/// markdown reply with a snapshot of the generated spec.
/// </summary>
public static class ReportCommand
{
    public static Command Create()
    {
        var issueBodyOption = new Option<FileInfo>("--issue-body")
        {
            Description = "Path to a file containing the GitHub issue body text.",
            Required = true,
        };
        var dataOption = new Option<DirectoryInfo>("--data")
        {
            Description = "Data repo root.",
            Required = true,
        };
        var dataSourceOption = new Option<string>("--data-source")
        {
            Description = "Data source URI the generated spec should target.",
            DefaultValueFactory = _ => "local:.",
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
        var outDirOption = new Option<DirectoryInfo>("--out-dir")
        {
            Description = "Directory to write reply.md, report.json, and snapshot.yaml into.",
            DefaultValueFactory = _ => new DirectoryInfo("."),
        };

        var command = new Command("report", "Evaluate a bug-report issue body and render a verdict reply.");
        command.Options.Add(issueBodyOption);
        command.Options.Add(dataOption);
        command.Options.Add(dataSourceOption);
        command.Options.Add(enginesOption);
        command.Options.Add(governingOption);
        command.Options.Add(outDirOption);
        command.SetAction((parse, ct) => Run(
            parse.GetValue(issueBodyOption)!,
            parse.GetValue(dataOption)!,
            parse.GetValue(dataSourceOption)!,
            parse.GetValue(enginesOption) ?? [],
            parse.GetValue(governingOption) ?? [],
            parse.GetValue(outDirOption),
            ct));

        return command;
    }

    internal static async Task<int> Run(
        FileInfo issueBody, DirectoryInfo data, string dataSource,
        string[] engines, string[] governing, DirectoryInfo? outDir, CancellationToken ct = default)
    {
        try
        {
            if (!issueBody.Exists)
            {
                Console.Error.WriteLine($"issue body file not found: {issueBody.FullName}");
                return 2;
            }

            if (!data.Exists)
            {
                Console.Error.WriteLine($"data directory not found: {data.FullName}");
                return 2;
            }

            var outDirPath = outDir?.FullName ?? ".";
            Directory.CreateDirectory(outDirPath);

            var body = IssueBody.Parse(await File.ReadAllTextAsync(issueBody.FullName, ct));

            ReplayRoster? roster = null;
            string? specYaml = null;
            string? error = null;
            var inlineSpec = false;

            switch (body.Roster)
            {
                case null:
                    error = "no roster found: accepted formats are a New Recruit share link, a .ros/.rosz attachment, or an inline fenced yaml code block containing a steps: list";
                    break;

                case { Kind: RosterSourceKind.NrLink } src:
                {
                    // Reviewer-mandated: re-validate before fetching. IssueBody's discovery
                    // regex is kept in lockstep with NrShareLink's, but a caller-supplied
                    // RosterSource must never be trusted to have gone through IssueBody.Parse.
                    if (!NrShareLink.TryParse(src.Value, out var key))
                    {
                        error = "the New Recruit link in this report is not a valid share link";
                        break;
                    }

                    using var client = new NrClient();
                    var fetched = await client.FetchListAsync(key, ct);
                    if (fetched.Json is null)
                    {
                        error = fetched.Error;
                        break;
                    }

                    try
                    {
                        roster = NrListParser.Parse(fetched.Json);
                    }
                    catch (FormatException e)
                    {
                        error = e.Message;
                    }

                    break;
                }

                case { Kind: RosterSourceKind.Attachment } src:
                {
                    using var client = new AttachmentClient();
                    var (bytes, downloadError) = await client.DownloadAsync(src.Value, ct);
                    if (bytes is null)
                    {
                        error = downloadError;
                        break;
                    }

                    try
                    {
                        using var stream = new MemoryStream(bytes);
                        var fileName = src.Value[(src.Value.LastIndexOf('/') + 1)..];
                        roster = RosterFileConverter.Convert(stream, fileName);
                    }
                    catch (FormatException e)
                    {
                        error = e.Message;
                    }

                    break;
                }

                case { Kind: RosterSourceKind.InlineYaml } src:
                {
                    // The pasted YAML *is* the spec: validate it via SpecLoader, but write it
                    // through verbatim (no re-emit) — roster stays null and VerdictMapper is
                    // told via inlineSpec so it doesn't treat that as "no roster found".
                    inlineSpec = true;
                    try
                    {
                        SpecLoader.LoadFromYaml(src.Value, defaultId: "report");
                        specYaml = src.Value;
                    }
                    catch (Exception e)
                    {
                        error = $"the inline spec is not valid: {e.Message}";
                    }

                    break;
                }
            }

            if (roster is not null)
            {
                specYaml = SpecEmitter.Emit(roster, specId: "report", dataSource, pinObserved: true);
            }

            MultiRunReport? runs = null;
            if (specYaml is not null && error is null)
            {
                var tempFixtures = Directory.CreateTempSubdirectory("muster-report-");
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(tempFixtures.FullName, "report.yaml"), specYaml, ct);
                    runs = MultiRunReport.Run(data.FullName, tempFixtures.FullName, engines, governing);
                }
                finally
                {
                    tempFixtures.Delete(recursive: true);
                }
            }

            var verdict = VerdictMapper.Map(roster, error, runs, inlineSpec);
            var reply = ReplyRenderer.Render(verdict, roster, runs, specYaml ?? "", body.Problem, body.Expected);

            await File.WriteAllTextAsync(Path.Combine(outDirPath, "reply.md"), reply, ct);
            await File.WriteAllTextAsync(Path.Combine(outDirPath, "report.json"), ToJson(verdict, runs), ct);
            if (specYaml is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(outDirPath, "snapshot.yaml"), specYaml, ct);
            }

            Console.Out.WriteLine(reply);
            return 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string ToJson(Verdict verdict, MultiRunReport? runs) => JsonSerializer.Serialize(
        new
        {
            verdict = verdict.Labels[0],
            labels = verdict.Labels,
            engineGap = verdict.EngineGap,
            governing = runs?.Governing,
        },
        JsonOptions);
}
