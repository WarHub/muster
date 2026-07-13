using System.CommandLine;

namespace Muster.Cli;

public static class Program
{
    internal static async Task<int> Main(string[] args)
        => await CreateRootCommand().Parse(args).InvokeAsync();

    public static RootCommand CreateRootCommand() =>
        new("muster — your data passes muster. CI toolchain for wargame data repos.");
}
