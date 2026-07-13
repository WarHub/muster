using Xunit;

namespace Muster.Cli.Tests;

/// <summary>
/// Tests that call <see cref="Console.SetOut"/> to capture stdout must not run concurrently
/// with each other — Console.Out is a shared, process-wide static. Sharing this collection
/// serializes them (xunit never parallelizes tests within the same collection) without
/// disabling parallelism for the rest of the test assembly.
/// </summary>
[CollectionDefinition("Console output tests")]
public sealed class ConsoleOutputTestCollection;
