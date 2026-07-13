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
        var previousReplyOption = new Option<FileInfo?>("--previous-reply")
        {
            Description = "Path to the previous sticky-comment reply body (if any). When this " +
                "evaluation produces no spec of its own, the durable snapshot embedded in the " +
                "previous reply is carried forward instead of being replaced with an empty one.",
        };

        var command = new Command("report", "Evaluate a bug-report issue body and render a verdict reply.");
        command.Options.Add(issueBodyOption);
        command.Options.Add(dataOption);
        command.Options.Add(dataSourceOption);
        command.Options.Add(enginesOption);
        command.Options.Add(governingOption);
        command.Options.Add(outDirOption);
        command.Options.Add(previousReplyOption);
        command.SetAction((parse, ct) => Run(
            parse.GetValue(issueBodyOption)!,
            parse.GetValue(dataOption)!,
            parse.GetValue(dataSourceOption)!,
            parse.GetValue(enginesOption) ?? [],
            parse.GetValue(governingOption) ?? [],
            parse.GetValue(outDirOption),
            parse.GetValue(previousReplyOption),
            ct));

        return command;
    }

    internal static async Task<int> Run(
        FileInfo issueBody, DirectoryInfo data, string dataSource,
        string[] engines, string[] governing, DirectoryInfo? outDir, FileInfo? previousReply = null,
        CancellationToken ct = default)
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
                        var inline = SpecLoader.LoadFromYaml(src.Value, defaultId: "report");

                        // Hermeticity: a hostile inline spec could declare its own
                        // setup.dataSource (e.g. "local:/etc" or another repo's path) to make
                        // the engine read arbitrary container-reachable paths instead of this
                        // repository's data. A dataSource that doesn't match the one this
                        // workflow is running against is rejected outright; an absent/empty
                        // dataSource (a fully self-contained inline setup) is unaffected.
                        if (inline.Setup.DataSource is { Length: > 0 } inlineDataSource
                            && !string.Equals(inlineDataSource, dataSource, StringComparison.Ordinal))
                        {
                            error = "inline spec declares a dataSource that does not match this repository's data source";
                        }
                        else
                        {
                            specYaml = src.Value;
                        }
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

            // This evaluation produced no spec of its own (roster/spec conversion failed, or no
            // roster was found at all). Sticky-comment replies are posted with edit-mode:
            // replace on a SINGLE comment, so if we render an empty snapshot here it destroys
            // the only durable copy of a spec captured by an earlier, successful evaluation —
            // making promotion permanently impossible even after the report is fixed. Carry the
            // previous reply's snapshot forward instead (never re-run engines against it here —
            // it is stale by construction).
            string? carriedYaml = null;
            if (specYaml is null && previousReply is { Exists: true })
            {
                var previousBody = await File.ReadAllTextAsync(previousReply.FullName, ct);
                carriedYaml = SnapshotExtractor.ExtractFromBody(previousBody);
            }

            var carriedForward = carriedYaml is not null;
            var effectiveSpecYaml = specYaml ?? carriedYaml ?? "";

            var verdict = VerdictMapper.Map(roster, error, runs, inlineSpec);
            var reply = ReplyRenderer.Render(
                verdict, roster, runs, effectiveSpecYaml, body.Problem, body.Expected, carriedForward);

            await File.WriteAllTextAsync(Path.Combine(outDirPath, "reply.md"), reply, ct);
            await File.WriteAllTextAsync(Path.Combine(outDirPath, "report.json"), ToJson(verdict, runs), ct);
            if (effectiveSpecYaml.Length > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(outDirPath, "snapshot.yaml"), effectiveSpecYaml, ct);
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
