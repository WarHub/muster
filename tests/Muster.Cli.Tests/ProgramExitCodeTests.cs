using Xunit;

namespace Muster.Cli.Tests;

/// <summary>
/// Usage errors (a malformed invocation) must never be confused with a fixture-run failure:
/// exit code 1 is reserved for "fixtures found a real regression". Parse errors — missing
/// required options, unknown subcommands, bad arguments — get remapped to exit code 2
/// (inconclusive family) by <see cref="Program.Main"/>. <c>--help</c> and <c>--version</c>
/// are not parse errors and must keep exiting 0.
/// </summary>
[Collection("Console output tests")]
public class ProgramExitCodeTests
{
    [Fact]
    public async Task Missing_required_options_exit_inconclusive_not_failed()
    {
        var exit = await RunCaptured(["test"]);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Unknown_subcommand_exits_inconclusive_not_failed()
    {
        var exit = await RunCaptured(["bogus"]);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Version_option_exits_zero()
    {
        var exit = await RunCaptured(["--version"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Help_option_exits_zero()
    {
        var exit = await RunCaptured(["--help"]);
        Assert.Equal(0, exit);
    }

    private static async Task<int> RunCaptured(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(new StringWriter());
            return await Program.Main(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
