using System.CommandLine;
using System.CommandLine.Invocation;
using Xunit;

namespace Muster.Cli.Tests;

public class SmokeTests
{
    [Fact]
    public void Version_option_prints_non_empty_version_string()
    {
        var root = Program.CreateRootCommand();
        var parseResult = root.Parse(["--version"]);
        var output = new StringWriter();
        var config = new InvocationConfiguration { Output = output };

        var exitCode = parseResult.Invoke(config);

        Assert.Equal(0, exitCode);
        var text = output.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
