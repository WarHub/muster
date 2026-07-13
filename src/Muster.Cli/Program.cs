using System.CommandLine;
using Muster.Cli.Commands;

namespace Muster.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parseResult = CreateRootCommand().Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // A parse error (missing required option, unknown subcommand, bad argument, ...) means
        // the invocation itself was malformed. The default pipeline already prints the errors
        // and help text and returns exit code 1 - but 1 is reserved for "fixture failure" in
        // this CLI's exit-code contract. Remap parse errors into the inconclusive family (2) so
        // a misconfigured invocation can never be mistaken for a data regression. --help and
        // --version are not parse errors (Errors is empty for them) so they keep exit code 0.
        return parseResult.Errors.Count > 0 ? 2 : exitCode;
    }

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("muster — your data passes muster. CI toolchain for wargame data repos.");
        root.Subcommands.Add(TestCommand.Create());
        root.Subcommands.Add(DiffCommand.Create());
        return root;
    }
}
