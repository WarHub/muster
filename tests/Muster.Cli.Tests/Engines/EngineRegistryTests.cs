using Muster.Cli.Engines;
using Xunit;

namespace Muster.Cli.Tests.Engines;

public class EngineRegistryTests
{
    [Theory]
    [InlineData("wham", "wham", EngineKind.Builtin, null, null)]
    [InlineData("battlescribe=dotnet:/opt/adapter/Ref.dll", "battlescribe", EngineKind.Exec, "dotnet", "/opt/adapter/Ref.dll")]
    [InlineData("newrecruit=docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest", "newrecruit", EngineKind.Docker, "docker", "run -i --rm ghcr.io/warhub/bsspec-adapter-newrecruit:latest")]
    [InlineData("custom=/usr/bin/my-adapter --flag", "custom", EngineKind.Exec, "/usr/bin/my-adapter", "--flag")]
    public void Parse_handles_all_command_forms(string input, string name, EngineKind kind, string? exe, string? args)
    {
        var spec = EngineSpec.Parse(input);
        Assert.Equal(name, spec.Name);
        Assert.Equal(kind, spec.Kind);
        Assert.Equal(exe, spec.Executable);
        Assert.Equal(args, spec.Arguments);
    }

    [Fact]
    public void Parse_rejects_builtin_command_for_unknown_name()
    {
        // bare name with no command is only valid for the builtin engine
        Assert.Throws<ArgumentException>(() => EngineSpec.Parse("newrecruit"));
    }

    [Fact]
    public void ParseAll_defaults_to_builtin_wham()
    {
        var engines = EngineRegistry.ParseAll([]);
        var e = Assert.Single(engines);
        Assert.Equal("wham", e.Name);
        Assert.Equal(EngineKind.Builtin, e.Kind);
    }

    [Fact]
    public void Builtin_wham_is_available_and_creates_adapter()
    {
        var spec = EngineSpec.Parse("wham");
        Assert.True(EngineRegistry.IsAvailable(spec));
        using var engine = EngineRegistry.CreateEngine(spec);
        Assert.NotNull(engine);
    }

    [Fact]
    public void Missing_executable_is_unavailable_not_a_crash()
    {
        var spec = EngineSpec.Parse("ghost=/no/such/binary-xyz");
        Assert.False(EngineRegistry.IsAvailable(spec));
    }

    [Fact]
    public void ResolveGoverning_picks_first_precedence_entry_that_ran()
    {
        Assert.Equal("battlescribe", EngineRegistry.ResolveGoverning(
            ["newrecruit", "battlescribe", "wham"], ["wham", "battlescribe"]));
        Assert.Equal("wham", EngineRegistry.ResolveGoverning(
            ["newrecruit", "battlescribe", "wham"], ["wham"]));
        Assert.Null(EngineRegistry.ResolveGoverning(
            ["newrecruit"], ["wham"]));
    }
}
