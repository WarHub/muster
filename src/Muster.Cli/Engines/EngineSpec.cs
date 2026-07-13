namespace Muster.Cli.Engines;

public enum EngineKind { Builtin, Exec, Docker }

/// <summary>
/// One engine registration: <c>wham</c> (builtin) or
/// <c>name=dotnet:path.dll</c> / <c>name=docker:image</c> / <c>name=exe [args]</c>.
/// </summary>
public sealed record EngineSpec(string Name, EngineKind Kind, string? Executable, string? Arguments)
{
    public const string BuiltinName = "wham";

    public static EngineSpec Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var eq = spec.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            if (!string.Equals(spec.Trim(), BuiltinName, StringComparison.Ordinal))
                throw new ArgumentException($"engine '{spec}' has no adapter command; only '{BuiltinName}' is builtin");
            return new(BuiltinName, EngineKind.Builtin, null, null);
        }

        var name = spec[..eq].Trim();
        var command = spec[(eq + 1)..].Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (command.StartsWith("dotnet:", StringComparison.Ordinal))
            return new(name, EngineKind.Exec, "dotnet", command["dotnet:".Length..]);
        if (command.StartsWith("docker:", StringComparison.Ordinal))
            return new(name, EngineKind.Docker, "docker", $"run -i --rm {command["docker:".Length..]}");

        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? new(name, EngineKind.Exec, command, null)
            : new(name, EngineKind.Exec, command[..space], command[(space + 1)..]);
    }
}
