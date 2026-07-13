// NDJSON adapter host serving FakeRosterEngine — lets tests exercise the
// out-of-proc engine path (AdapterProcess + JsonProtocolEngine) hermetically.
// FAKE_PTS env var sets the per-selection pts value (default 20).
using BattleScribeSpec.Protocol;
using Muster.Cli.Tests.Fakes;

var pts = decimal.TryParse(Environment.GetEnvironmentVariable("FAKE_PTS"), out var v) ? v : 20m;

await AdapterHandler.RunAsync(
    engineFactory: () => new FakeRosterEngine(pts),
    input: Console.In,
    output: Console.Out);
