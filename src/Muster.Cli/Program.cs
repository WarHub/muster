using System.CommandLine;
using Muster.Cli.Commands;

namespace Muster.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
        => await CreateRootCommand().Parse(args).InvokeAsync();

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("muster — your data passes muster. CI toolchain for wargame data repos.");
        root.Subcommands.Add(TestCommand.Create());
        root.Subcommands.Add(DiffCommand.Create());
        return root;
    }
}
