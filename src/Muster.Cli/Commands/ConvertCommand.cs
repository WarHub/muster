using System.CommandLine;
using Muster.Cli.Converters;
using Muster.Cli.NewRecruit;

namespace Muster.Cli.Commands;

/// <summary>
/// <c>muster convert &lt;input&gt; [--id &lt;spec-id&gt;] [--data-source &lt;uri&gt;] [--pin-observed &lt;bool&gt;] [-o &lt;file&gt;]</c>
/// — converts a roster file (.ros, .rosz, .json) or New Recruit share link to a fixture-DSL spec.
/// </summary>
public static class ConvertCommand
{
    public static Command Create()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "File path (.ros, .rosz, .json) or New Recruit share URL.",
        };
        var idOption = new Option<string?>("--id")
        {
            Description = "Spec ID (defaults to input file stem or nr-<key>).",
        };
        var dataSourceOption = new Option<string>("--data-source")
        {
            Description = "Data source URI.",
            DefaultValueFactory = _ => "local:.",
        };
        var pinObservedOption = new Option<bool>("--pin-observed")
        {
            Description = "Include observed totals as expectedState (default: true).",
            DefaultValueFactory = _ => true,
        };
        var outputOption = new Option<FileInfo?>("--output", ["-o"])
        {
            Description = "Output file (default: stdout).",
        };

        var command = new Command("convert", "Convert roster file or New Recruit link to fixture-DSL spec.");
        command.Arguments.Add(inputArg);
        command.Options.Add(idOption);
        command.Options.Add(dataSourceOption);
        command.Options.Add(pinObservedOption);
        command.Options.Add(outputOption);
        command.SetAction((parse, ct) => Run(
            parse.GetValue(inputArg)!,
            parse.GetValue(idOption),
            parse.GetValue(dataSourceOption)!,
            parse.GetValue(pinObservedOption),
            parse.GetValue(outputOption),
            ct));

        return command;
    }

    internal static async Task<int> Run(
        string input, string? id, string dataSource, bool pinObserved, FileInfo? output,
        CancellationToken ct = default)
    {
        try
        {
            ReplayRoster roster;
            string defaultId;
            if (File.Exists(input))
            {
                defaultId = Path.GetFileNameWithoutExtension(input);
                var ext = Path.GetExtension(input);
                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    roster = NrListParser.Parse(await File.ReadAllTextAsync(input, ct));
                }
                else if (ext.Equals(".ros", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".rosz", StringComparison.OrdinalIgnoreCase))
                {
                    await using var stream = File.OpenRead(input);
                    roster = RosterFileConverter.Convert(stream, input);
                }
                else
                {
                    Console.Error.WriteLine($"unsupported input extension: {ext}");
                    return 2;
                }
            }
            else if (NrShareLink.TryParse(input, out var key))
            {
                defaultId = $"nr-{key}";
                using var client = new NrClient();
                var fetched = await client.FetchListAsync(key, ct);
                if (fetched.Json is null)
                {
                    Console.Error.WriteLine(fetched.Error);
                    return 2;
                }
                roster = NrListParser.Parse(fetched.Json);
            }
            else
            {
                Console.Error.WriteLine($"input is neither an existing file nor a New Recruit share link: {input}");
                return 2;
            }

            var yaml = SpecEmitter.Emit(roster, id ?? defaultId, dataSource, pinObserved);
            if (output is null)
                Console.Out.Write(yaml);
            else
                await File.WriteAllTextAsync(output.FullName, yaml, ct);
            return 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }
}
