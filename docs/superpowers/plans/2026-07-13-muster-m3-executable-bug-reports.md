# Muster M3 — Executable Bug Reports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bug-report rosters (NR share links, `.ros`/`.rosz`, inline YAML) become executable specs evaluated per-engine with a configurable governing engine; issues get auto-labeled verdict replies with durable snapshots; maintainers promote fixed bugs to golden fixtures via `/muster promote`; `muster diff` gains `--fail-on-broke` (muster#4).

**Architecture:** Everything becomes a spec — converters emit fixture-DSL YAML with assertions pinning the reporter's *observed* values; the existing `RosterRunner` pipeline executes them per engine via an engine registry (builtin wham in-proc; external adapters over the NDJSON protocol via `AdapterProcess`+`JsonProtocolEngine`, including `docker:<image>` commands). One engine *governs* each verdict (default precedence `newrecruit > battlescribe > wham`); engine disagreement raises `engine-gap`.

**Tech Stack:** .NET 10 (SDK 10.0.100), System.CommandLine 2.0.3, xunit.v3 3.2.2, System.Text.Json (BCL), TestKit (`BattleScribeSpec.TestKit` project ref), wham (`WarHub.ArmouryModel.*` project refs). No new package references in Muster.Cli.

**Spec:** `docs/superpowers/specs/2026-07-13-muster-m3-executable-bug-reports-design.md` (approved 2026-07-13).

## Global Constraints

- Exit-code discipline: **0** = evaluated/reply produced, **1** = genuine fixture failure (test) or governing-engine broke with `--fail-on-broke` (diff), **2** = harness error/usage error/inconclusive. A crash must NEVER surface as exit 1.
- `TreatWarningsAsErrors=true` — **always verify `dotnet build -c Release`**, not just Debug; CA analyzers are Release-enforced.
- Hermeticity: the `RepoDataSourceResolver.IsPopulatedFor` gate stays in front of every fixture evaluation, all engines. The ONLY permitted network calls are the NR share-link fetch and GitHub attachment downloads, both behind `UrlAllowlist`.
- Hostile input: issue bodies are stranger-controlled. Caps (verbatim from spec §6): fetch timeout 30 s, response size cap 5 MB, JSON depth cap 64, node-count cap 20 000, roster selection-count cap 5 000. Exceeding any → `needs-info`, never a crash. No stack traces in replies.
- Licensing (hard): nothing in muster's public artifacts may embed or download the proprietary BattleScribe JARs. The public Docker image ships the wham engine only.
- Engine names are exactly `wham`, `battlescribe`, `newrecruit` (lowercase). Default governing precedence exactly `newrecruit > battlescribe > wham`.
- Verdict labels are exactly: `confirmed`, `not-reproducible`, `needs-info`, `inconclusive`, `engine-gap`.
- Blast-radius classification strings stay exactly: `unchanged`, `broke`, `fixed`, `still-failing`, `verdict-changed`, `inconclusive`.
- String comparisons: `StringComparer.Ordinal` / `StringComparison.Ordinal` unless matching user text.
- External repos: all battlescribe-spec changes go through the nested submodule at `lib/wham/lib/battlescribe-spec` on branch `feat/muster-support`, bumped through wham branch `feat/yaml-reader`; PRs opened at the end. Never touch `D:\repos\battlescribe-spec` (separate checkout with uncommitted user work).
- WarHub org ruleset requires PRs for ALL branch updates — never push directly to `main`; each muster CI iteration needs a fresh branch + PR.
- Markdown comment markers: report reply `<!-- muster:report -->`, snapshot block `<!-- muster:snapshot -->` — exact strings, used for sticky-comment lookup and snapshot extraction.

## Reference: key existing interfaces (verified 2026-07-13)

- `TestCommand.RunFixtures(string dataDir, string fixturesDir)` → `RunReport` — shared machinery (`src/Muster.Cli/Commands/TestCommand.cs:88`); constructs `new SpecRosterEngineAdapter()` per fixture, `new RosterRunner(engine, resolver, engineName: "wham")`.
- `RosterRunner(IRosterEngine engine, DataSourceResolver? resolver = null, string? engineName = null)`; `SpecResult Run(SpecFile spec)`; public `Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>? OnStepCompleted`. Action step exceptions set `SpecResult.HarnessError`; assertion mismatches only append `Failures` (failure strings start `"Step {index}: …"`).
- `SpecResult(string SpecId, string Category, string Description, IReadOnlyList<string> Failures)` + `string? HarnessError { get; init; }`; `Passed => Failures.Count == 0`.
- `SpecLoader.Load(string path)`, `SpecLoader.LoadFromYaml(string yaml, string? defaultId = null)` — CamelCase YamlDotNet binding; **no SpecFile→YAML serializer exists** (emit YAML text by hand, round-trip-validate via `LoadFromYaml`).
- `expectedState.costs` asserts **roster-level totals** (`RosterState.Costs`, aggregated across forces — comparer `RosterRunner.AssertExpectedState`, cost block `RosterRunner.cs:310-347`). Per-selection: `forces[].selections[].costs`. `expectedState.engines.<name>` = per-engine override merged by `ExpectedStateDef.ForEngine(engineName)`.
- DSL step actions + YAML args: `addForce{forceEntryId, catalogueId?}`, `addChildForce{forceId, forceEntryId, catalogueId?}`, `selectEntry{forceId, entryId}` → outputs `selectionId`+`selections` map, `selectChildEntry{forceId, selectionId, entryId}`, `setSelectionCount{forceId, selectionId, count}`, `setCustomization{forceId, selectionId?, categoryEntryId?, customName?, customNotes?}`, `setCostLimit{costTypeId, value}`. Instance-ID fields accept `${{ steps.<id>.forceId }}` / `${{ steps.<id>.selectionId }}`; definition-ID fields are literal.
- Out-of-proc engines: `AdapterProcess.Start(string executable, string? arguments = null)` + `new JsonProtocolEngine(adapterProcess)` (implements `IRosterEngine`; 30 s default per-command timeout) — both in `BattleScribeSpec.TestKit/Protocol/`.
- `.ros` load: `BattleScribeXml.LoadRoster(string path)` / `(Stream)` (`WarHub.ArmouryModel.Source.BattleScribe`); `.rosz`: `stream.LoadSourceAuto(filename)` (`XmlFileExtensions`, `Workspaces.BattleScribe`) — returns `SourceNode?`, cast to `RosterNode`.
- `RosterNode`: `Name, GameSystemId, Costs (NodeList<CostNode>), CostLimits, Forces`. `ForceNode`: `CatalogueId, Categories, Forces (nested), EntryId, Selections`. `SelectionNode`: `EntryId (composite "linkId::targetId" when via link), EntryGroupId, Number, Type, CustomName, Costs, Selections (nested)`. `CostNode`: `Name, TypeId, Value (decimal)`.
- NR list JSON (sample: `tests/Muster.Cli.Tests/TestData/nr-list-war-horde.json`, fetched live 2026-07-13): top-level `{name, totalCost, totalCosts[{name,value,typeId}], bsid_system, bsid_book, books_revision[], nrversion, army}`. `army.options[]` tree levels: catalogue node (`option_id` = catalogue id, no `catalogue_id` key) → force node (has `catalogue_id`, `option_id` = force entry id) → descendants. Node classifier (verified on all 120 nodes of the sample): **has `amount` ⇒ selection** (count = `amount`, entry id = `link_id::option_id` if `link_id` present else `option_id`); **no `amount` ⇒ transparent container** (category/group — recurse through, don't emit a step).
- NR fetch: `POST https://www.newrecruit.eu/api/rpc`, JSON body `{"method":"open_share_link","params":["<key>"]}`, `Content-Type: application/json` → list JSON, or `null` for missing/rotted lists.
- `DiffCommand.Run` currently ends `return 0;` (`DiffCommand.cs:72`) — the `--fail-on-broke` hook point.
- entrypoint.sh: positional `<data-path> <fixtures-path> [base-ref]`, `MUSTER_CMD` env override, `build_dataroot()` scans fixtures for `dataSource:` decls, exit-2 → `::warning::` + exit 0.

---

### Task 1: Shared FakeEngine + out-of-proc TestAdapter host

Test infrastructure everything later leans on: promote the private `FakeEngine` to a shared, configurable test double, and add a console host that serves it over the NDJSON protocol so the engine registry's out-of-proc path is testable without Docker or Playwright.

**Files:**
- Create: `tests/Muster.Cli.Tests/Fakes/FakeRosterEngine.cs`
- Create: `tests/Muster.TestAdapter/Muster.TestAdapter.csproj`
- Create: `tests/Muster.TestAdapter/Program.cs`
- Modify: `tests/Muster.Cli.Tests/RepoDataSourceResolverTests.cs` (replace private nested `FakeEngine` with the shared one)
- Modify: `Muster.slnx` (add Muster.TestAdapter)
- Test: `tests/Muster.Cli.Tests/Fakes/FakeRosterEngineTests.cs`

**Interfaces:**
- Consumes: `IRosterEngine`, `RosterState`, `CostState`, `ActionOutputs` (TestKit `Roster/`), `AdapterHandler.RunAsync` (TestKit `Protocol/AdapterHandler.cs:16`).
- Produces: `Muster.Cli.Tests.Fakes.FakeRosterEngine : IRosterEngine` — ctor `FakeRosterEngine(decimal ptsValue = 20m)`; tracks `SelectEntry`/`SetSelectionCount` calls; `GetRosterState()` returns a roster whose `Costs` contains `new CostState("pts", "pts", ptsValue * _totalCount)` where `_totalCount` = number of selections × their counts; `ReceivedFiles` property as before. `Muster.TestAdapter` console: env var `FAKE_PTS` (default `20`) controls `ptsValue` — a *divergent* engine is simply the same adapter launched with a different `FAKE_PTS`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Muster.Cli.Tests/Fakes/FakeRosterEngineTests.cs
using BattleScribeSpec.Roster;
using Muster.Cli.Tests.Fakes;

namespace Muster.Cli.Tests.Fakes;

public class FakeRosterEngineTests
{
    [Fact]
    public void Costs_scale_with_selection_count_and_pts_value()
    {
        using var engine = new FakeRosterEngine(ptsValue: 30m);
        engine.SetupFromFiles([("a.gst", "<gameSystem/>")]);
        var force = engine.AddForce("fe-1", "cat-1");
        var sel = engine.SelectEntry(force.ForceId!, "se-1");
        engine.SetSelectionCount(force.ForceId!, sel.SelectionId!, 2);

        var state = engine.GetRosterState();

        var pts = Assert.Single(state.Costs);
        Assert.Equal("pts", pts.TypeId);
        Assert.Equal(60m, pts.Value); // 30 pts × count 2
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Muster.Cli.Tests --filter FakeRosterEngineTests -v minimal`
Expected: FAIL — `FakeRosterEngine` does not exist (compile error).

- [ ] **Step 3: Implement FakeRosterEngine**

```csharp
// tests/Muster.Cli.Tests/Fakes/FakeRosterEngine.cs
using BattleScribeSpec.Roster;

namespace Muster.Cli.Tests.Fakes;

/// <summary>
/// Configurable in-proc engine double. Every selection contributes
/// <c>ptsValue × count</c> to a single roster-level "pts" cost, so tests can
/// pin totals and simulate divergent engines by varying <c>ptsValue</c>.
/// </summary>
public sealed class FakeRosterEngine(decimal ptsValue = 20m) : IRosterEngine
{
    private readonly List<(string SelectionId, int Count)> _selections = [];
    private int _nextId;

    public List<(string FileName, string Content)>? ReceivedFiles { get; private set; }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        ReceivedFiles = [.. files];
        return [];
    }

    public ActionOutputs AddForce(string forceEntryId, string catalogueId) =>
        new() { ForceId = $"force-{_nextId++}" };

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
        new() { ForceId = $"force-{_nextId++}" };

    public void RemoveForce(string forceId) { }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        var id = $"sel-{_nextId++}";
        _selections.Add((id, 1));
        return new() { SelectionId = id, Selections = [] };
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
        SelectEntry(forceId, entryId);

    public void DeselectSelection(string forceId, string selectionId) =>
        _selections.RemoveAll(s => s.SelectionId == selectionId);

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        var i = _selections.FindIndex(s => s.SelectionId == selectionId);
        if (i < 0) throw new InvalidOperationException($"unknown selection: {selectionId}");
        _selections[i] = (selectionId, count);
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        var id = $"sel-{_nextId++}";
        _selections.Add((id, 1));
        return new() { SelectionId = id };
    }

    public ActionOutputs DuplicateForce(string forceId) => new() { ForceId = $"force-{_nextId++}" };

    public void SetCostLimit(string costTypeId, decimal value) { }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes) { }

    public RosterState GetRosterState()
    {
        var total = _selections.Sum(s => s.Count) * ptsValue;
        return new RosterState("roster", "gs", [], [new CostState("pts", "pts", total)], []);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

    public void Dispose() { }
}
```

Note: `RosterState`'s positional order is `(Name, GameSystemId, Forces, Costs, ValidationErrors)` in the existing nested fake (`RepoDataSourceResolverTests.cs:202` passes `("roster", "gs", [], [], [])`) — check the record definition in `BattleScribeSpec.TestKit/Roster/RosterTypes.cs:17-24` (it has 5+ components incl. `CostLimits`) and match it exactly; adjust the constructor call if `CostLimits` is a sixth positional or init property.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Muster.Cli.Tests --filter FakeRosterEngineTests -v minimal`
Expected: PASS.

- [ ] **Step 5: Create the TestAdapter console host**

```xml
<!-- tests/Muster.TestAdapter/Muster.TestAdapter.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\lib\wham\lib\battlescribe-spec\src\BattleScribeSpec.TestKit\BattleScribeSpec.TestKit.csproj" />
    <Compile Include="..\Muster.Cli.Tests\Fakes\FakeRosterEngine.cs" Link="FakeRosterEngine.cs" />
  </ItemGroup>
</Project>
```

```csharp
// tests/Muster.TestAdapter/Program.cs
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
```

Add the project to `Muster.slnx` (follow the existing `<Project Path="..."/>` entries). Replace the private nested `FakeEngine` in `RepoDataSourceResolverTests.cs` with `Fakes.FakeRosterEngine` (delete the nested class; the existing test only needs `ReceivedFiles` + benign action behavior, both preserved).

- [ ] **Step 6: Verify the whole suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: all tests PASS (including `RepoDataSourceResolverTests`), Release build zero warnings.

- [ ] **Step 7: Commit**

```bash
git add tests Muster.slnx
git commit -m "test: shared FakeRosterEngine + NDJSON TestAdapter host"
```

---

### Task 2: Engine registry

`EngineSpec` parsing (`name`, `name=dotnet:path`, `name=docker:image`, `name=exe args`), availability probing, and `IRosterEngine` creation for builtin/exec/docker kinds. Governing-precedence resolution.

**Files:**
- Create: `src/Muster.Cli/Engines/EngineSpec.cs`
- Create: `src/Muster.Cli/Engines/EngineRegistry.cs`
- Test: `tests/Muster.Cli.Tests/Engines/EngineRegistryTests.cs`

**Interfaces:**
- Consumes: `SpecRosterEngineAdapter` (wham), `AdapterProcess.Start(string, string?)`, `JsonProtocolEngine(AdapterProcess)` (TestKit).
- Produces:
  - `sealed record EngineSpec(string Name, EngineKind Kind, string? Executable, string? Arguments)` with `enum EngineKind { Builtin, Exec, Docker }`; `static EngineSpec Parse(string spec)`.
  - `static class EngineRegistry`: `IReadOnlyList<EngineSpec> ParseAll(IReadOnlyList<string> specs)` (defaults to `[EngineSpec.Parse("wham")]` when empty); `bool IsAvailable(EngineSpec spec)`; `IRosterEngine CreateEngine(EngineSpec spec)`; `string? ResolveGoverning(IReadOnlyList<string> precedence, IReadOnlyList<string> ranEngines)` (first precedence entry present in `ranEngines`, ordinal; default precedence constant `DefaultGoverning = ["newrecruit", "battlescribe", "wham"]`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/Engines/EngineRegistryTests.cs
using Muster.Cli.Engines;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter EngineRegistryTests -v minimal`
Expected: FAIL (types don't exist).

- [ ] **Step 3: Implement**

```csharp
// src/Muster.Cli/Engines/EngineSpec.cs
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
```

```csharp
// src/Muster.Cli/Engines/EngineRegistry.cs
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using WarHub.ArmouryModel.RosterEngine.Spec;

namespace Muster.Cli.Engines;

public static class EngineRegistry
{
    public static readonly IReadOnlyList<string> DefaultGoverning = ["newrecruit", "battlescribe", "wham"];

    public static IReadOnlyList<EngineSpec> ParseAll(IReadOnlyList<string> specs) =>
        specs.Count == 0 ? [EngineSpec.Parse(EngineSpec.BuiltinName)] : [.. specs.Select(EngineSpec.Parse)];

    public static bool IsAvailable(EngineSpec spec) => spec.Kind switch
    {
        EngineKind.Builtin => true,
        EngineKind.Docker => CanStart("docker", "--version"),
        EngineKind.Exec when string.Equals(spec.Executable, "dotnet", StringComparison.Ordinal) =>
            spec.Arguments is { } args && File.Exists(FirstToken(args)),
        EngineKind.Exec => File.Exists(spec.Executable) || CanStart(spec.Executable!, "--version"),
        _ => false,
    };

    public static IRosterEngine CreateEngine(EngineSpec spec) => spec.Kind switch
    {
        EngineKind.Builtin => new SpecRosterEngineAdapter(),
        _ => new JsonProtocolEngine(AdapterProcess.Start(spec.Executable!, spec.Arguments)),
    };

    public static string? ResolveGoverning(IReadOnlyList<string> precedence, IReadOnlyList<string> ranEngines) =>
        precedence.FirstOrDefault(p => ranEngines.Contains(p, StringComparer.Ordinal));

    private static string FirstToken(string s)
    {
        var i = s.IndexOf(' ', StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }

    private static bool CanStart(string exe, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(5000);
            return p is { ExitCode: 0 };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
```

Check `JsonProtocolEngine`'s constructor signature in `BattleScribeSpec.TestKit/Protocol/JsonProtocolEngine.cs` (takes `AdapterProcess` + optional timeout); it owns disposal of the process via its own `Dispose` — verify and, if it does not dispose the `AdapterProcess`, wrap both in a small `CompositeDisposableEngine` returned from `CreateEngine`.

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Muster.Cli.Tests --filter EngineRegistryTests -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Muster.Cli/Engines tests/Muster.Cli.Tests/Engines
git commit -m "feat: engine registry — builtin/exec/docker engine specs, governing resolution"
```

---

### Task 3: Multi-engine `test` — engine-parameterized RunFixtures + MultiRunReport

`RunFixtures` gains an engine parameter; `test` gets `--engines`/`--governing`, loops engines, renders a per-engine report, and keeps exit discipline (1 on ANY engine's assertion failure — golden fixtures resolve expectations per engine via the DSL's `engines:` blocks).

**Files:**
- Modify: `src/Muster.Cli/Commands/TestCommand.cs`
- Create: `src/Muster.Cli/Reporting/MultiRunReport.cs`
- Modify: `src/Muster.Cli/Reporting/RunReport.cs` (no shape change; add nothing — writers for multi live in MultiRunReport)
- Test: `tests/Muster.Cli.Tests/MultiEngineTestCommandTests.cs`

**Interfaces:**
- Consumes: `EngineRegistry`, `EngineSpec` (Task 2); `TestCommand.RunFixtures` internals; `TestRepoFactory.CreateTestRepo()` (existing test helper: green fixture `unit-costs-20.yaml` pinning pts=20, data under `github/muster-e2e/test-data/main/`).
- Produces:
  - `TestCommand.RunFixtures(string dataDir, string fixturesDir, EngineSpec engine)` → `RunReport` (existing 2-arg overload delegates with builtin wham; `RunReport.Engine` carries `engine.Name`; the engine name is passed to `RosterRunner`'s `engineName` so DSL `engines:` overrides resolve).
  - `sealed record MultiRunReport(string? Governing, IReadOnlyList<string> Unavailable, IReadOnlyList<RunReport> Runs)` with `static MultiRunReport Run(string dataDir, string fixturesDir, IReadOnlyList<string> engineSpecs, IReadOnlyList<string> governing)` (parses, probes availability, runs each available engine, resolves governor), `static string ToJson(MultiRunReport)`, `static void Write(MultiRunReport, string mode, TextWriter)` (`summary` | `json` | `github-actions`; single-run degenerate output identical in spirit to today's).
  - Per-fixture-per-engine: reuses `FixtureResult` unchanged inside each engine's `RunReport`.
- Out-of-proc engines: ONE `IRosterEngine` per engine per run (AdapterProcess reused across fixtures; `RosterRunner` calls `Cleanup()` between specs). Builtin wham: fresh `SpecRosterEngineAdapter` per fixture (current behavior — cheap, in-proc).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/MultiEngineTestCommandTests.cs
using Muster.Cli.Commands;
using Muster.Cli.Reporting;

namespace Muster.Cli.Tests;

public class MultiEngineTestCommandTests
{
    private static string TestAdapterDll =>
        // built alongside the test assembly; artifacts output layout
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Muster.TestAdapter", "debug", "Muster.TestAdapter.dll"));

    [Fact]
    public void Two_engines_produce_two_runs_and_governing_resolves_by_precedence()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();

        var multi = MultiRunReport.Run(dataDir, fixturesDir,
            engineSpecs: ["wham", $"fake=dotnet:{TestAdapterDll}"],
            governing: ["fake", "wham"]);

        Assert.Equal(2, multi.Runs.Count);
        Assert.Equal("fake", multi.Governing);
        Assert.Contains(multi.Runs, r => r.Engine == "wham");
        Assert.Contains(multi.Runs, r => r.Engine == "fake");
    }

    [Fact]
    public void Unavailable_engine_is_named_not_dropped()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();

        var multi = MultiRunReport.Run(dataDir, fixturesDir,
            engineSpecs: ["wham", "ghost=/no/such/adapter-xyz"],
            governing: ["ghost", "wham"]);

        Assert.Single(multi.Runs);
        Assert.Equal(["ghost"], multi.Unavailable);
        Assert.Equal("wham", multi.Governing); // ghost didn't run, precedence falls through
    }

    [Fact]
    public void Github_actions_output_renders_engine_matrix()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var multi = MultiRunReport.Run(dataDir, fixturesDir, ["wham"], ["wham"]);

        using var sw = new StringWriter();
        MultiRunReport.Write(multi, "github-actions", sw);
        var text = sw.ToString();

        Assert.Contains("wham", text, StringComparison.Ordinal);
        Assert.Contains("governing", text, StringComparison.OrdinalIgnoreCase);
    }
}
```

Note on `TestAdapterDll`: verify the actual artifacts path after building (`UseArtifactsOutput=true` → `artifacts/bin/Muster.TestAdapter/debug/Muster.TestAdapter.dll` from repo root). Compute it robustly: walk up from `AppContext.BaseDirectory` to the directory containing `Muster.slnx`, then `artifacts/bin/Muster.TestAdapter/debug/Muster.TestAdapter.dll`. Adjust the helper accordingly — the two candidate layouts must be checked in Step 3, not guessed.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter MultiEngineTestCommandTests -v minimal`
Expected: FAIL (MultiRunReport does not exist).

- [ ] **Step 3: Implement**

`TestCommand.cs` changes:
- Extract the per-fixture body so the engine is injected. New signature (keep old one delegating):

```csharp
internal static RunReport RunFixtures(string dataDir, string fixturesDir) =>
    RunFixtures(dataDir, fixturesDir, EngineSpec.Parse(EngineSpec.BuiltinName));

internal static RunReport RunFixtures(string dataDir, string fixturesDir, EngineSpec engineSpec)
{
    // existing validation + fixture discovery + resolver creation unchanged
    // …
    // Builtin: fresh adapter per fixture (today's behavior).
    // External: one engine instance for the whole run, reused across fixtures.
    IRosterEngine? sharedEngine = engineSpec.Kind == EngineKind.Builtin ? null : EngineRegistry.CreateEngine(engineSpec);
    try
    {
        var results = new List<FixtureResult>();
        foreach (var path in fixturePaths)
            results.Add(RunFixture(path, dataDir, resolver, engineSpec, sharedEngine));
        return RunReport.Create(engineSpec.Name, dataDir, results);
    }
    finally
    {
        sharedEngine?.Dispose();
    }
}
```

`RunFixture` gains `(EngineSpec engineSpec, IRosterEngine? sharedEngine)`; inside:

```csharp
IRosterEngine engine = sharedEngine ?? new SpecRosterEngineAdapter();
try
{
    var runner = new RosterRunner(engine, resolver, engineName: engineSpec.Name);
    var result = runner.Run(spec);
    // existing HarnessError / result mapping unchanged
}
finally
{
    if (sharedEngine is null) engine.Dispose();
}
```

`MultiRunReport.cs`:

```csharp
// src/Muster.Cli/Reporting/MultiRunReport.cs
using System.Text.Json;
using Muster.Cli.Commands;
using Muster.Cli.Engines;

namespace Muster.Cli.Reporting;

public sealed record MultiRunReport(string? Governing, IReadOnlyList<string> Unavailable, IReadOnlyList<RunReport> Runs)
{
    public static MultiRunReport Run(string dataDir, string fixturesDir,
        IReadOnlyList<string> engineSpecs, IReadOnlyList<string> governing)
    {
        var specs = EngineRegistry.ParseAll(engineSpecs);
        var unavailable = new List<string>();
        var runs = new List<RunReport>();
        foreach (var spec in specs)
        {
            if (!EngineRegistry.IsAvailable(spec)) { unavailable.Add(spec.Name); continue; }
            runs.Add(TestCommand.RunFixtures(dataDir, fixturesDir, spec));
        }
        var precedence = governing.Count > 0 ? governing : EngineRegistry.DefaultGoverning;
        var governor = EngineRegistry.ResolveGoverning(precedence, [.. runs.Select(r => r.Engine)]);
        return new(governor, unavailable, runs);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ToJson(MultiRunReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static void Write(MultiRunReport report, string mode, TextWriter writer)
    {
        if (mode == "json") { writer.WriteLine(ToJson(report)); return; }
        foreach (var run in report.Runs)
        {
            writer.WriteLine(mode == "github-actions"
                ? $"### Engine: {run.Engine}{(run.Engine == report.Governing ? " (governing)" : "")}"
                : $"-- engine: {run.Engine}{(run.Engine == report.Governing ? " (governing)" : "")} --");
            RunReport.Write(run, mode, writer);
            writer.WriteLine();
        }
        foreach (var name in report.Unavailable)
            writer.WriteLine(mode == "github-actions"
                ? $"> ⚠ engine `{name}` was requested but is unavailable in this environment."
                : $"[????] engine {name}: unavailable");
    }
}
```

`TestCommand.Create()` additions (System.CommandLine 2.0.3 object-initializer style, matching existing options):

```csharp
var enginesOption = new Option<string[]>("--engines")
{
    Description = "Engines to run: 'wham' (builtin), 'name=dotnet:path.dll', 'name=docker:image', 'name=exe args'. Default: wham.",
    AllowMultipleArgumentsPerToken = true,
};
var governingOption = new Option<string[]>("--governing")
{
    Description = "Governing-engine precedence (first match that ran governs). Default: newrecruit battlescribe wham.",
    AllowMultipleArgumentsPerToken = true,
};
```

`Run` uses `MultiRunReport.Run(...)`; exit code: `1` if ANY run has `Failed > 0`; else `2` if any run has `Inconclusive > 0` or (`Runs.Count == 0`); else `0`. `--report` writes `MultiRunReport.ToJson`.

- [ ] **Step 4: Run the full suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: PASS (existing `TestCommandTests`/`DiffCommandTests` unchanged — 2-arg `RunFixtures` still works; single-engine JSON report shape check in `TestCommandTests` may need updating if `--report` output changed to MultiRunReport — update that test's assertions to the new `{governing, unavailable, runs:[…]}` shape).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: multi-engine test — engine-parameterized RunFixtures, MultiRunReport, --engines/--governing"
```

---

### Task 4: Multi-engine `diff` + `--fail-on-broke` + engine-gap + Action inputs (muster#4)

Per-engine blast radius; `--fail-on-broke` gates on the governing engine's rows; cross-engine head-status disagreement surfaces as an `engine-gap` note; `action.yml` + `entrypoint.sh` wire `fail-on-broke` (default **true**), `fail-on-inconclusive` (default false), `engines`, `governing`.

**Files:**
- Modify: `src/Muster.Cli/Commands/DiffCommand.cs`
- Modify: `src/Muster.Cli/Reporting/BlastRadius.cs` (add multi-engine writer; `Classify` stays pure and single-engine — called per engine)
- Modify: `action.yml`, `entrypoint.sh`
- Test: `tests/Muster.Cli.Tests/DiffCommandTests.cs` (extend), `tests/Muster.Cli.Tests/BlastRadiusTests.cs` (extend)

**Interfaces:**
- Consumes: `MultiRunReport.Run` (Task 3), `BlastRadius.Classify(RunReport, RunReport)` (existing, unchanged).
- Produces: `sealed record EngineDiff(string Engine, IReadOnlyList<BlastRow> Rows)`; `sealed record MultiDiffReport(string? Governing, IReadOnlyList<string> Unavailable, IReadOnlyList<EngineDiff> Diffs, IReadOnlyList<string> EngineGaps)`; `BlastRadius.ClassifyMulti(MultiRunReport baseRuns, MultiRunReport headRuns)` → `MultiDiffReport` (pairs engines by name; engine present on only one side → listed in `Unavailable`, excluded from rows and gating); `EngineGaps` = fixture ids where head status differs across engines that ran. `DiffCommand` exit: with `--fail-on-broke`, exit 1 iff the governing engine's rows contain `broke` or `verdict-changed`; else existing semantics (0 when both runs completed).

- [ ] **Step 1: Write the failing tests**

```csharp
// append to tests/Muster.Cli.Tests/DiffCommandTests.cs
[Fact]
public void FailOnBroke_exits_1_when_governing_engine_broke()
{
    var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
    var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25"); // breaks the pts=20 pin

    var exit = RunDiff(dataDir, headData, fixturesDir, "markdown",
        failOnBroke: true, engines: ["wham"], governing: ["wham"]);

    Assert.Equal(1, exit);
}

[Fact]
public void FailOnBroke_ignores_non_governing_break()
{
    var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
    var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25");
    // fake adapter always computes pts=20 → fixture passes both sides for 'fake';
    // wham (non-governing) breaks; check must stay green but report the gap.
    var exit = RunDiff(dataDir, headData, fixturesDir, "markdown",
        failOnBroke: true,
        engines: [$"fake=dotnet:{TestAdapterDll}", "wham"],
        governing: ["fake", "wham"]);

    Assert.Equal(0, exit);
}

[Fact]
public void Head_status_disagreement_reports_engine_gap()
{
    var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
    var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25");

    var output = CaptureDiffOutput(dataDir, headData, fixturesDir, "markdown",
        engines: [$"fake=dotnet:{TestAdapterDll}", "wham"], governing: ["fake"]);

    Assert.Contains("engine-gap", output, StringComparison.Ordinal);
}
```

(`RunDiff`/`CaptureDiffOutput`/`TestAdapterDll` are small local helpers invoking `DiffCommand.Run` with the new parameters — write them in the test file; `TestAdapterDll` shared via a tiny `TestPaths` helper class if Task 3 already created one.)

**Caveat for the fake-engine scenario:** the green fixture pins `pts=20` and `FakeRosterEngine` computes `20 × count` — confirm the fixture in `TestRepoFactory` executes exactly one `selectEntry` with count 1 (read `TestRepoFactory.cs` first; adjust `FAKE_PTS` via an env-var pass-through on the engine spec if the fixture shape differs: `AdapterProcess.Start` inherits the parent environment, so set `Environment.SetEnvironmentVariable("FAKE_PTS", "20")` in the test before running).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter DiffCommandTests -v minimal`
Expected: FAIL (new parameters don't exist).

- [ ] **Step 3: Implement**

`BlastRadius.cs` — add (below the existing single-engine members, which stay untouched):

```csharp
public sealed record EngineDiff(string Engine, IReadOnlyList<BlastRow> Rows);

public sealed record MultiDiffReport(
    string? Governing,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<EngineDiff> Diffs,
    IReadOnlyList<string> EngineGaps);

public static MultiDiffReport ClassifyMulti(MultiRunReport baseRuns, MultiRunReport headRuns)
{
    var baseByEngine = baseRuns.Runs.ToDictionary(r => r.Engine, StringComparer.Ordinal);
    var headByEngine = headRuns.Runs.ToDictionary(r => r.Engine, StringComparer.Ordinal);

    var unavailable = new List<string>(baseRuns.Unavailable.Union(headRuns.Unavailable, StringComparer.Ordinal));
    var diffs = new List<EngineDiff>();
    foreach (var (engine, baseRun) in baseByEngine)
    {
        if (!headByEngine.TryGetValue(engine, out var headRun)) { unavailable.Add(engine); continue; }
        diffs.Add(new(engine, Classify(baseRun, headRun)));
    }
    foreach (var engine in headByEngine.Keys.Except(baseByEngine.Keys, StringComparer.Ordinal))
        unavailable.Add(engine);

    // engine-gap: fixtures whose HEAD status differs across engines that ran both sides
    var gaps = new List<string>();
    if (diffs.Count > 1)
    {
        var byFixture = diffs
            .SelectMany(d => d.Rows.Select(r => (r.FixtureId, r.HeadStatus)))
            .GroupBy(x => x.FixtureId, StringComparer.Ordinal);
        foreach (var g in byFixture)
            if (g.Select(x => x.HeadStatus).Distinct(StringComparer.Ordinal).Count() > 1)
                gaps.Add(g.Key);
    }

    return new(headRuns.Governing, unavailable, diffs, gaps);
}
```

Add `WriteMulti(MultiDiffReport report, string mode, TextWriter writer)`: `json` → serialize the record (reuse `JsonOptions`); `markdown` → per-engine `### Engine: {name}` sections reusing the existing single-engine markdown body, then, when `EngineGaps.Count > 0`, a section:

```markdown
### ⚠ engine-gap
Engines disagree on the head state of: `fixture-a`, `fixture-b`. The governing
engine's verdict stands; divergence should be triaged as an engine defect.
```

`DiffCommand.cs`: add `--engines`, `--governing`, `--fail-on-broke` (`Option<bool>`, default false); `Run` signature becomes
`internal static int Run(DirectoryInfo baseDir, DirectoryInfo headDir, DirectoryInfo fixtures, string output, bool failOnBroke, string[] engines, string[] governing)`;
body: two `MultiRunReport.Run` calls (base/head), `BlastRadius.ClassifyMulti`, `WriteMulti`; then:

```csharp
if (failOnBroke && report.Governing is { } gov)
{
    var governingRows = report.Diffs.FirstOrDefault(d => d.Engine == gov)?.Rows ?? [];
    if (governingRows.Any(r => r.Classification is "broke" or "verdict-changed"))
        return 1;
}
return 0;
```

`action.yml` — add inputs (after `base-ref`):

```yaml
  fail-on-broke:
    description: Fail the check when the governing engine classifies any fixture as broke/verdict-changed (diff mode).
    default: "true"
  fail-on-inconclusive:
    description: Fail the check (exit 1) instead of neutral warning when the run is inconclusive.
    default: "false"
  engines:
    description: Space-separated engine specs (e.g. "wham newrecruit=docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest").
    default: "wham"
  governing:
    description: Space-separated governing precedence.
    default: "newrecruit battlescribe wham"
```

and append them to `runs.args` (positional 4–7).

`entrypoint.sh` — accept the new positionals with defaults, build flag arrays:

```bash
FAIL_ON_BROKE="${4:-true}"
FAIL_ON_INCONCLUSIVE="${5:-false}"
ENGINES_INPUT="${6:-wham}"
GOVERNING_INPUT="${7:-newrecruit battlescribe wham}"

ENGINE_ARGS=(--engines)
read -r -a ENGINE_LIST <<< "$ENGINES_INPUT"
ENGINE_ARGS+=("${ENGINE_LIST[@]}")
GOVERNING_ARGS=(--governing)
read -r -a GOVERNING_LIST <<< "$GOVERNING_INPUT"
GOVERNING_ARGS+=("${GOVERNING_LIST[@]}")
```

pass `"${ENGINE_ARGS[@]}" "${GOVERNING_ARGS[@]}"` to both `muster test` and `muster diff`; add `--fail-on-broke` to the diff invocation when `FAIL_ON_BROKE == "true"`; change the exit-2 mapping:

```bash
if [[ "$rc" -eq 2 ]]; then
  if [[ "$FAIL_ON_INCONCLUSIVE" == "true" ]]; then
    echo "::error::muster run was inconclusive (exit 2) and fail-on-inconclusive is set"
    exit 1
  fi
  echo "::warning::muster run was inconclusive (exit 2) -- treating as neutral"
  exit 0
fi
```

- [ ] **Step 4: Run the full suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: PASS. The pre-existing `Diff_reports_broken_fixture_between_trees` (exit 0 without the flag) must STILL pass — CLI default is unchanged; only the Action default flips.

- [ ] **Step 5: Commit and close the loop on muster#4**

```bash
git add src tests action.yml entrypoint.sh
git commit -m "feat: per-engine diff, --fail-on-broke governing gate, engine-gap surfacing (closes #4)"
```

---

### Task 5: NR share-link fetcher

`NrShareLink.TryParse` (strict allowlist) + `NrClient` (POST `open_share_link`, caps, graceful failure). Pure HTTP layer — no conversion.

**Files:**
- Create: `src/Muster.Cli/NewRecruit/NrShareLink.cs`
- Create: `src/Muster.Cli/NewRecruit/NrClient.cs`
- Test: `tests/Muster.Cli.Tests/NewRecruit/NrShareLinkTests.cs`, `tests/Muster.Cli.Tests/NewRecruit/NrClientTests.cs`

**Interfaces:**
- Produces:
  - `static class NrShareLink`: `bool TryParse(string url, out string key)` — accepts EXACTLY `https://www.newrecruit.eu/app/list/<key>` (optional single trailing slash) where `<key>` matches `^[A-Za-z0-9]{1,32}$`; https only, exact host `www.newrecruit.eu`, no query strings.
  - `sealed class NrClient(HttpMessageHandler? handler = null) : IDisposable`: `Task<NrFetchResult> FetchListAsync(string key, CancellationToken ct = default)`; `sealed record NrFetchResult(string? Json, string? Error)` — `Json` null on any failure with a human-readable `Error`. Never throws for remote/protocol problems. RPC returning literal `null`/`[]`/empty ⇒ "list not found or no longer shared" error.
  - Caps: timeout 30 s, max response 5 MB (streamed read with cap check).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/NewRecruit/NrShareLinkTests.cs
using Muster.Cli.NewRecruit;

namespace Muster.Cli.Tests.NewRecruit;

public class NrShareLinkTests
{
    [Theory]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd", true, "3Pbpd")]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd/", true, "3Pbpd")]
    [InlineData("https://www.newrecruit.eu/app/list/tr5BL", true, "tr5BL")]
    [InlineData("http://www.newrecruit.eu/app/list/3Pbpd", false, "")]
    [InlineData("https://newrecruit.eu/app/list/3Pbpd", false, "")]
    [InlineData("https://evil.example/app/list/3Pbpd", false, "")]
    [InlineData("https://www.newrecruit.eu/app/list/../secret", false, "")]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd?x=1", false, "")]
    [InlineData("https://www.newrecruit.eu/api/rpc", false, "")]
    [InlineData("not a url", false, "")]
    public void TryParse_allowlist(string url, bool ok, string key)
    {
        Assert.Equal(ok, NrShareLink.TryParse(url, out var k));
        if (ok) Assert.Equal(key, k);
    }
}
```

```csharp
// tests/Muster.Cli.Tests/NewRecruit/NrClientTests.cs
using System.Net;
using System.Text;
using Muster.Cli.NewRecruit;

namespace Muster.Cli.Tests.NewRecruit;

public class NrClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    [Fact]
    public async Task Fetch_posts_open_share_link_rpc_and_returns_json()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"war horde","army":{}}""", Encoding.UTF8, "application/json"),
        });
        using var client = new NrClient(handler);

        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Contains("war horde", result.Json, StringComparison.Ordinal);
        Assert.Equal("https://www.newrecruit.eu/api/rpc", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"open_share_link\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"3Pbpd\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_rpc_response_is_not_found()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("null") });
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("gone1", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.Contains("no longer shared", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_error_is_graceful()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.InternalServerError));
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var big = new string('x', 6 * 1024 * 1024);
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent($"{{\"pad\":\"{big}\"}}") });
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.Contains("too large", result.Error, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter "NrShareLinkTests|NrClientTests" -v minimal`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement**

```csharp
// src/Muster.Cli/NewRecruit/NrShareLink.cs
using System.Text.RegularExpressions;

namespace Muster.Cli.NewRecruit;

public static partial class NrShareLink
{
    [GeneratedRegex(@"^https://www\.newrecruit\.eu/app/list/([A-Za-z0-9]{1,32})/?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string url, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = Pattern().Match(url.Trim());
        if (!m.Success) return false;
        key = m.Groups[1].Value;
        return true;
    }
}
```

```csharp
// src/Muster.Cli/NewRecruit/NrClient.cs
using System.Text;
using System.Text.Json;

namespace Muster.Cli.NewRecruit;

public sealed record NrFetchResult(string? Json, string? Error);

/// <summary>
/// Fetches a shared New Recruit list via the (undocumented) open_share_link RPC.
/// All remote failures degrade to <see cref="NrFetchResult.Error"/> — callers
/// map them to needs-info, never a crash.
/// </summary>
public sealed class NrClient(HttpMessageHandler? handler = null) : IDisposable
{
    private const long MaxResponseBytes = 5 * 1024 * 1024;
    private static readonly Uri RpcUri = new("https://www.newrecruit.eu/api/rpc");

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<NrFetchResult> FetchListAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["method"] = "open_share_link",
                ["params"] = new[] { key },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, RpcUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new(null, $"New Recruit returned HTTP {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                limited.Write(buffer, 0, read);
                if (limited.Length > MaxResponseBytes)
                    return new(null, "response too large (>5 MB)");
            }
            var text = Encoding.UTF8.GetString(limited.ToArray()).Trim();
            if (text is "null" or "" or "[]")
                return new(null, "list not found or no longer shared (New Recruit share links expire)");
            if (!text.StartsWith('{'))
                return new(null, "New Recruit returned an unexpected response");
            return new(text, null);
        }
        catch (TaskCanceledException)
        {
            return new(null, "New Recruit did not respond within 30 seconds");
        }
        catch (HttpRequestException e)
        {
            return new(null, $"could not reach New Recruit: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Muster.Cli.Tests --filter "NrShareLinkTests|NrClientTests" -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Muster.Cli/NewRecruit tests/Muster.Cli.Tests/NewRecruit
git commit -m "feat: NR share-link parser (strict allowlist) + open_share_link client with caps"
```

---

### Task 6: NR list parser + spec emitter (the "everything becomes a spec" core)

`NrListParser` (System.Text.Json, hostile-input caps) → intermediate `ReplayRoster` tree → `SpecEmitter` renders fixture-DSL YAML (hand-emitted text — no TestKit serializer exists; every path is round-trip-validated with `SpecLoader.LoadFromYaml`). Real sample is already committed at `tests/Muster.Cli.Tests/TestData/nr-list-war-horde.json`.

**Files:**
- Create: `src/Muster.Cli/Converters/ReplayRoster.cs`
- Create: `src/Muster.Cli/Converters/NrListParser.cs`
- Create: `src/Muster.Cli/Converters/SpecEmitter.cs`
- Modify: `tests/Muster.Cli.Tests/Muster.Cli.Tests.csproj` (add `<Content Include="TestData/**" CopyToOutputDirectory="PreserveNewest" />`)
- Test: `tests/Muster.Cli.Tests/Converters/NrListParserTests.cs`, `tests/Muster.Cli.Tests/Converters/SpecEmitterTests.cs`

**Interfaces:**
- Produces (shared intermediate model, also consumed by Task 7's `.ros` converter and Task 9/10's report flow):
  - `sealed record ReplayCost(string Name, string TypeId, decimal Value)`
  - `sealed record ReplaySelection(string EntryId, int Count, string? CustomName, IReadOnlyList<ReplayCost> ObservedCosts, IReadOnlyList<ReplaySelection> Children)` — `EntryId` already composite (`linkId::targetId`) where applicable
  - `sealed record ReplayForce(string ForceEntryId, string CatalogueId, IReadOnlyList<ReplaySelection> Selections, IReadOnlyList<ReplayForce> ChildForces)`
  - `sealed record ReplayRoster(string Name, string? GameSystemId, IReadOnlyList<ReplayCost> ObservedTotals, IReadOnlyList<string> BooksRevisions, IReadOnlyList<ReplayForce> Forces, IReadOnlyList<string> Unmapped)` — non-empty `Unmapped` ⇒ report verdict `needs-info` (spec rule: partial replay never yields confirmed/not-reproducible)
  - `static class NrListParser`: `ReplayRoster Parse(string json)` — throws `FormatException` (safe message) on malformed/hostile input. Caps: JSON depth 64, nodes 20 000, selections 5 000.
  - `static class SpecEmitter`: `string Emit(ReplayRoster roster, string specId, string dataSource, bool pinObserved)`. Emitted spec: `category: report`; steps use ids `force-1…`/`sel-1…`; selections reference `${{ steps.force-N.forceId }}` / parent `${{ steps.sel-N.selectionId }}`; `count != 1` → `setSelectionCount`; `CustomName` → `setCustomization`; final step `expectedState.costs` pins `ObservedTotals` when `pinObserved`.
- NR node classification (verified on all 120 nodes of the committed sample): `army.options[]` level 1 = catalogue node (`option_id` = catalogue id) → children carrying `catalogue_id` key = force nodes (`option_id` = force entry id) → descendants: **has `amount` ⇒ selection** (entry id = `link_id::option_id` when `link_id` present, else `option_id`; count = `amount`); **no `amount` ⇒ transparent container** (category/group — recurse through, emit nothing).

- [ ] **Step 1: Write the failing parser tests**

```csharp
// tests/Muster.Cli.Tests/Converters/NrListParserTests.cs
using Muster.Cli.Converters;

namespace Muster.Cli.Tests.Converters;

public class NrListParserTests
{
    private static string SampleJson => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "nr-list-war-horde.json"));

    [Fact]
    public void Parses_war_horde_sample()
    {
        var roster = NrListParser.Parse(SampleJson);

        Assert.Equal("war horde", roster.Name);
        var pts = Assert.Single(roster.ObservedTotals);
        Assert.Equal(950m, pts.Value);
        Assert.Equal("51b2-306e-1021-d207", pts.TypeId);
        var force = Assert.Single(roster.Forces);
        Assert.Equal("bb9d-299a-ed60-2d8a", force.ForceEntryId);
        Assert.Equal("a55f-b7b3-6c65-a05f", force.CatalogueId);
        Assert.NotEmpty(force.Selections);
        Assert.Empty(roster.Unmapped);
        Assert.Contains("Xenos - Orks: 2", roster.BooksRevisions);
    }

    [Fact]
    public void Linked_selection_gets_composite_entry_id()
    {
        var roster = NrListParser.Parse(SampleJson);
        var all = Flatten(roster);
        // "Battle Size" selection: option_id 564e-fbc6-5266-3ea4 selected via link 7380-3e40-6ed6-b7cc
        Assert.Contains(all, s => s.EntryId == "7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4");
    }

    [Fact]
    public void Container_nodes_are_transparent()
    {
        var roster = NrListParser.Parse(SampleJson);
        var all = Flatten(roster);
        // "Configuration" (category node, no amount) must not appear as a selection…
        Assert.DoesNotContain(all, s => s.EntryId.Contains("4ac9-fd30-1e3d-b249", StringComparison.Ordinal));
        // …but "Incursion", nested category → selection → group → child, must
        Assert.Contains(all, s => s.EntryId.EndsWith("d62d-db22-4893-4bc0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"army":{"options":[]},"name":"x"}""")]
    public void Hostile_or_empty_input_throws_FormatException(string json) =>
        Assert.Throws<FormatException>(() => NrListParser.Parse(json));

    private static List<ReplaySelection> Flatten(ReplayRoster r)
    {
        var result = new List<ReplaySelection>();
        void Walk(IEnumerable<ReplaySelection> sels)
        {
            foreach (var s in sels) { result.Add(s); Walk(s.Children); }
        }
        foreach (var f in r.Forces) Walk(f.Selections);
        return result;
    }
}
```

Add a depth-cap test as well: build a pathologically nested `options` chain programmatically (a loop wrapping `{"name":"n","option_id":"x","amount":1,"options":[…]}` 100 times inside a minimal valid catalogue/force envelope) and assert `FormatException`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter NrListParserTests -v minimal`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement ReplayRoster + NrListParser**

```csharp
// src/Muster.Cli/Converters/ReplayRoster.cs
namespace Muster.Cli.Converters;

public sealed record ReplayCost(string Name, string TypeId, decimal Value);

public sealed record ReplaySelection(
    string EntryId, int Count, string? CustomName,
    IReadOnlyList<ReplayCost> ObservedCosts,
    IReadOnlyList<ReplaySelection> Children);

public sealed record ReplayForce(
    string ForceEntryId, string CatalogueId,
    IReadOnlyList<ReplaySelection> Selections,
    IReadOnlyList<ReplayForce> ChildForces);

public sealed record ReplayRoster(
    string Name, string? GameSystemId,
    IReadOnlyList<ReplayCost> ObservedTotals,
    IReadOnlyList<string> BooksRevisions,
    IReadOnlyList<ReplayForce> Forces,
    IReadOnlyList<string> Unmapped);
```

```csharp
// src/Muster.Cli/Converters/NrListParser.cs
using System.Text.Json;

namespace Muster.Cli.Converters;

/// <summary>
/// Parses a New Recruit shared-list JSON payload into a ReplayRoster.
/// Node classification (verified against real NR data 2026-07-13):
/// army.options[] level 1 = catalogue; its children carrying "catalogue_id"
/// are forces; below that, nodes with "amount" are selections (entry id =
/// "link_id::option_id" when linked), nodes without are transparent
/// containers (categories / selection-entry-groups).
/// </summary>
public static class NrListParser
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 20_000;
    private const int MaxSelections = 5_000;

    public static ReplayRoster Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException e)
        {
            throw new FormatException($"not a valid New Recruit list: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("army", out var army))
                throw new FormatException("not a New Recruit list: missing 'army'");

            var name = GetString(root, "name") ?? "unnamed roster";
            var totals = ParseCosts(root, "totalCosts");
            var revisions = root.TryGetProperty("books_revision", out var revs) && revs.ValueKind == JsonValueKind.Array
                ? [.. revs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)]
                : (List<string>)[];
            var gameSystemId = GetString(root, "bsid_system");

            var state = new ParseState();
            var forces = new List<ReplayForce>();
            foreach (var catNode in Options(army))
            {
                state.CountNode();
                var catalogueId = GetString(catNode, "option_id")
                    ?? throw new FormatException("catalogue node missing option_id");
                foreach (var child in Options(catNode))
                {
                    state.CountNode();
                    if (child.TryGetProperty("catalogue_id", out _))
                        forces.Add(ParseForce(child, catalogueId, state));
                    else
                        state.Unmapped.Add($"unexpected non-force node '{GetString(child, "name")}' under catalogue");
                }
            }

            if (forces.Count == 0)
                throw new FormatException("no forces found in the list");

            return new(name, gameSystemId, totals, revisions, forces, state.Unmapped);
        }
    }

    private sealed class ParseState
    {
        public List<string> Unmapped { get; } = [];
        public int Selections;
        private int _nodes;

        public void CountNode()
        {
            if (++_nodes > MaxNodes) throw new FormatException($"list too large (over {MaxNodes} nodes)");
        }
    }

    private static ReplayForce ParseForce(JsonElement node, string catalogueId, ParseState state)
    {
        var forceEntryId = GetString(node, "option_id") ?? throw new FormatException("force node missing option_id");
        var selections = new List<ReplaySelection>();
        CollectSelections(node, selections, state, depth: 0);
        return new(forceEntryId, catalogueId, selections, ChildForces: []);
    }

    private static void CollectSelections(JsonElement node, List<ReplaySelection> into, ParseState state, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("list too deeply nested");
        foreach (var child in Options(node))
        {
            state.CountNode();
            if (child.TryGetProperty("amount", out var amountEl))
            {
                if (++state.Selections > MaxSelections)
                    throw new FormatException($"list too large (over {MaxSelections} selections)");
                var optionId = GetString(child, "option_id");
                if (optionId is null)
                {
                    state.Unmapped.Add($"selection '{GetString(child, "name")}' missing option_id");
                    continue;
                }
                var linkId = GetString(child, "link_id");
                var entryId = linkId is null ? optionId : $"{linkId}::{optionId}";
                var count = amountEl.ValueKind == JsonValueKind.Number ? amountEl.GetInt32() : 1;
                var children = new List<ReplaySelection>();
                CollectSelections(child, children, state, depth + 1);
                into.Add(new(entryId, count, GetString(child, "customName"), ObservedCosts: [], children));
            }
            else
            {
                CollectSelections(child, into, state, depth + 1);
            }
        }
    }

    private static IEnumerable<JsonElement> Options(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Array
            ? options.EnumerateArray() : [];

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<ReplayCost> ParseCosts(JsonElement el, string property)
    {
        var result = new List<ReplayCost>();
        if (el.TryGetProperty(property, out var costs) && costs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in costs.EnumerateArray())
            {
                var typeId = GetString(c, "typeId");
                if (typeId is null) continue;
                var value = c.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
                result.Add(new(GetString(c, "name") ?? typeId, typeId, value));
            }
        }
        return result;
    }
}
```

Run: `dotnet test tests/Muster.Cli.Tests --filter NrListParserTests -v minimal` — iterate until PASS.

- [ ] **Step 4: Write the failing emitter tests**

```csharp
// tests/Muster.Cli.Tests/Converters/SpecEmitterTests.cs
using BattleScribeSpec;
using Muster.Cli.Converters;

namespace Muster.Cli.Tests.Converters;

public class SpecEmitterTests
{
    private static ReplayRoster Sample() => new(
        Name: "war horde",
        GameSystemId: "sys-1",
        ObservedTotals: [new("pts", "51b2-306e-1021-d207", 950m)],
        BooksRevisions: ["Xenos - Orks: 2"],
        Forces:
        [
            new("bb9d-299a-ed60-2d8a", "a55f-b7b3-6c65-a05f",
                Selections:
                [
                    new("7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4", 1, null, [],
                        Children: [new("d62d-db22-4893-4bc0", 1, null, [], [])]),
                    new("boy-entry", 3, "Da Ladz", [new("pts", "pts-id", 60m)], []),
                ],
                ChildForces: []),
        ],
        Unmapped: []);

    [Fact]
    public void Emitted_yaml_round_trips_through_SpecLoader()
    {
        var yaml = SpecEmitter.Emit(Sample(), "issue-42", "github:test/repo", pinObserved: true);
        var spec = SpecLoader.LoadFromYaml(yaml); // throws on invalid spec

        Assert.Equal("issue-42", spec.Id);
        Assert.Equal("github:test/repo", spec.Setup.DataSource);
        Assert.Contains(spec.Steps, s => s.Action == "addForce" && s.ForceEntryId == "bb9d-299a-ed60-2d8a");
        Assert.Contains(spec.Steps, s => s.Action == "selectEntry" && s.EntryId == "7380-3e40-6ed6-b7cc::564e-fbc6-5266-3ea4");
        Assert.Contains(spec.Steps, s => s.Action == "selectChildEntry" && s.EntryId == "d62d-db22-4893-4bc0");
        Assert.Contains(spec.Steps, s => s.Action == "setSelectionCount" && s.Count == 3);
        Assert.Contains(spec.Steps, s => s.Action == "setCustomization" && s.CustomName == "Da Ladz");
        var assertStep = spec.Steps.Last();
        Assert.NotNull(assertStep.ExpectedState);
        var cost = Assert.Single(assertStep.ExpectedState!.Costs!);
        Assert.Equal(950m, cost.Value);
        Assert.Equal("51b2-306e-1021-d207", cost.TypeId);
    }

    [Fact]
    public void Without_pins_no_expectedState_is_emitted()
    {
        var yaml = SpecEmitter.Emit(Sample(), "issue-42", "github:test/repo", pinObserved: false);
        var spec = SpecLoader.LoadFromYaml(yaml);
        Assert.DoesNotContain(spec.Steps, s => s.ExpectedState is not null);
    }

    [Fact]
    public void Yaml_special_characters_are_escaped()
    {
        var roster = Sample() with { Name = "list: with \"quotes\" & #hash" };
        var yaml = SpecEmitter.Emit(roster, "x", "local:.", pinObserved: true);
        Assert.NotNull(SpecLoader.LoadFromYaml(yaml)); // must not throw
    }

    [Fact]
    public void Selection_steps_reference_parent_outputs()
    {
        var yaml = SpecEmitter.Emit(Sample(), "x", "local:.", pinObserved: false);
        Assert.Contains("${{ steps.sel-1.selectionId }}", yaml, StringComparison.Ordinal);
        Assert.Contains("${{ steps.force-1.forceId }}", yaml, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 5: Implement SpecEmitter**

```csharp
// src/Muster.Cli/Converters/SpecEmitter.cs
using System.Globalization;
using System.Text;

namespace Muster.Cli.Converters;

/// <summary>
/// Emits fixture-DSL YAML from a ReplayRoster. TestKit has no SpecFile→YAML
/// serializer, so YAML is emitted as text; every caller path is validated by
/// round-tripping through SpecLoader.LoadFromYaml in tests.
/// Step ids: force-1, force-2, … / sel-1, sel-2, … in document order.
/// All string scalars are double-quoted — sidesteps YAML coercion traps.
/// </summary>
public static class SpecEmitter
{
    public static string Emit(ReplayRoster roster, string specId, string dataSource, bool pinObserved)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"id: {Quote(specId)}");
        sb.AppendLine("category: report");
        sb.AppendLine($"description: {Quote($"Converted from bug report roster '{roster.Name}'")}");
        sb.AppendLine("setup:");
        sb.AppendLine($"  dataSource: {Quote(dataSource)}");
        sb.AppendLine();
        sb.AppendLine("steps:");

        var forceIndex = 0;
        var selIndex = 0;
        foreach (var force in roster.Forces)
            EmitForce(sb, force, parentForceStep: null, ref forceIndex, ref selIndex);

        if (pinObserved && roster.ObservedTotals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  # observed values from the report — PASS means the reported state reproduces");
            sb.AppendLine("  - expectedState:");
            sb.AppendLine("      costs:");
            foreach (var cost in roster.ObservedTotals)
            {
                sb.AppendLine($"        - typeId: {Quote(cost.TypeId)}");
                sb.AppendLine($"          value: {cost.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        return sb.ToString();
    }

    private static void EmitForce(StringBuilder sb, ReplayForce force, string? parentForceStep, ref int forceIndex, ref int selIndex)
    {
        forceIndex++;
        var stepId = $"force-{forceIndex}";
        if (parentForceStep is null)
        {
            sb.AppendLine("  - action: addForce");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceEntryId: {Quote(force.ForceEntryId)}");
            sb.AppendLine($"    catalogueId: {Quote(force.CatalogueId)}");
        }
        else
        {
            sb.AppendLine("  - action: addChildForce");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {Quote($"${{{{ steps.{parentForceStep}.forceId }}}}")}");
            sb.AppendLine($"    forceEntryId: {Quote(force.ForceEntryId)}");
            sb.AppendLine($"    catalogueId: {Quote(force.CatalogueId)}");
        }

        foreach (var sel in force.Selections)
            EmitSelection(sb, sel, forceStep: stepId, parentSelStep: null, ref selIndex);

        foreach (var child in force.ChildForces)
            EmitForce(sb, child, stepId, ref forceIndex, ref selIndex);
    }

    private static void EmitSelection(StringBuilder sb, ReplaySelection sel, string forceStep, string? parentSelStep, ref int selIndex)
    {
        selIndex++;
        var stepId = $"sel-{selIndex}";
        var forceRef = Quote($"${{{{ steps.{forceStep}.forceId }}}}");
        if (parentSelStep is null)
        {
            sb.AppendLine("  - action: selectEntry");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    entryId: {Quote(sel.EntryId)}");
        }
        else
        {
            sb.AppendLine("  - action: selectChildEntry");
            sb.AppendLine($"    id: {Quote(stepId)}");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{parentSelStep}.selectionId }}}}")}");
            sb.AppendLine($"    entryId: {Quote(sel.EntryId)}");
        }

        if (sel.Count != 1)
        {
            sb.AppendLine("  - action: setSelectionCount");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{stepId}.selectionId }}}}")}");
            sb.AppendLine($"    count: {sel.Count}");
        }

        if (!string.IsNullOrEmpty(sel.CustomName))
        {
            sb.AppendLine("  - action: setCustomization");
            sb.AppendLine($"    forceId: {forceRef}");
            sb.AppendLine($"    selectionId: {Quote($"${{{{ steps.{stepId}.selectionId }}}}")}");
            sb.AppendLine($"    customName: {Quote(sel.CustomName!)}");
        }

        foreach (var child in sel.Children)
            EmitSelection(sb, child, forceStep, stepId, ref selIndex);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
```

Check while implementing: whether the ExpressionResolver resolves `${{ … }}` inside a double-quoted YAML scalar (it operates on the deserialized string value, so quoting is transparent — but verify with the round-trip test asserting `s.ForceId == "${{ steps.force-1.forceId }}"` after load). If `StepDef.Id` is not a quoted-string-friendly field, drop `Quote` around step ids (they are `[a-z0-9-]+`, safe bare).

Per-selection observed costs (`ObservedCosts` on `ReplaySelection`) are carried in the model but NOT emitted as assertions in this task — Task 7 decides after reading `RosterRunner.AssertSelections` matching semantics (name vs index). Roster totals are the v1 pin.

- [ ] **Step 6: Run tests + Release build**

Run: `dotnet test tests/Muster.Cli.Tests --filter "NrListParserTests|SpecEmitterTests" -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Muster.Cli/Converters tests/Muster.Cli.Tests/Converters tests/Muster.Cli.Tests/Muster.Cli.Tests.csproj
git commit -m "feat: NR list parser + fixture-DSL spec emitter with observed-value pins"
```

---

### Task 7: `.ros`/`.rosz` → ReplayRoster converter

Walks a wham `RosterNode` into the same `ReplayRoster` intermediate, with per-selection observed costs (richer than NR). Decides per-selection pin emission after reading the comparer.

**Files:**
- Create: `src/Muster.Cli/Converters/RosterFileConverter.cs`
- Modify: `src/Muster.Cli/Muster.Cli.csproj` (add ProjectReferences: `WarHub.ArmouryModel.Source.BattleScribe`, `WarHub.ArmouryModel.Workspaces.BattleScribe` — check first whether they arrive transitively via `RosterEngine.Spec`; add only what's missing)
- Modify: `src/Muster.Cli/Converters/SpecEmitter.cs` (per-selection pins IF viable — see Step 4)
- Test: `tests/Muster.Cli.Tests/Converters/RosterFileConverterTests.cs`

**Interfaces:**
- Consumes: `BattleScribeXml.LoadRoster(Stream)` (`WarHub.ArmouryModel.Source.BattleScribe/BattleScribeXml.cs:70`), `XmlFileExtensions.LoadSourceAuto(this Stream, string filename, CancellationToken)` (`Workspaces.BattleScribe/XmlFileExtensions.cs:225`, handles `.rosz` zip with exactly-one-entry rule), `RosterNode` model (Costs/CostLimits/Forces; ForceNode.CatalogueId/EntryId/Selections/Forces; SelectionNode.EntryId (already composite `link::target`)/Number/CustomName/Costs/Selections).
- Produces: `static class RosterFileConverter`: `ReplayRoster Convert(Stream stream, string fileName)` — dispatches `.ros` (plain XML) vs `.rosz` (zipped) by extension; throws `FormatException` (safe message) on unparseable input; selection-count cap 5 000 applies here too.

- [ ] **Step 1: Write the failing test**

The test builds a minimal `.ros` XML inline (BattleScribe roster namespace) so no binary asset is needed:

```csharp
// tests/Muster.Cli.Tests/Converters/RosterFileConverterTests.cs
using System.IO.Compression;
using System.Text;
using Muster.Cli.Converters;

namespace Muster.Cli.Tests.Converters;

public class RosterFileConverterTests
{
    // Minimal valid BattleScribe roster XML. Namespace/version: match what
    // BattleScribeXml.LoadRoster expects — copy the exact xmlns and
    // battleScribeVersion from an existing wham test asset
    // (lib/wham/tests/**/ *.ros or the RosterCore XmlRoot: RootElementNames.Roster,
    // Namespaces.RosterXmlns). Adjust the constant if LoadRoster returns null.
    private const string RosterXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <roster id="r-1" name="Test Roster" battleScribeVersion="2.03"
                gameSystemId="gs-1" gameSystemName="Test GS" gameSystemRevision="1"
                xmlns="http://www.battlescribe.net/schema/rosterSchema">
          <costs>
            <cost name="pts" typeId="pts-type" value="65.0"/>
          </costs>
          <forces>
            <force id="f-1" name="Patrol" entryId="fe-1" catalogueId="cat-1"
                   catalogueRevision="1" catalogueName="Test Cat">
              <selections>
                <selection id="s-1" name="Deathmarks" entryId="link-1::se-1"
                           number="1" type="unit">
                  <costs>
                    <cost name="pts" typeId="pts-type" value="65.0"/>
                  </costs>
                  <selections>
                    <selection id="s-2" name="Gauss blaster" entryId="se-2"
                               number="5" type="upgrade" customName="Fancy guns"/>
                  </selections>
                </selection>
              </selections>
            </force>
          </forces>
        </roster>
        """;

    [Fact]
    public void Converts_ros_xml_to_replay_roster()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RosterXml));
        var roster = RosterFileConverter.Convert(stream, "test.ros");

        Assert.Equal("Test Roster", roster.Name);
        Assert.Equal("gs-1", roster.GameSystemId);
        var total = Assert.Single(roster.ObservedTotals);
        Assert.Equal(65.0m, total.Value);
        var force = Assert.Single(roster.Forces);
        Assert.Equal("fe-1", force.ForceEntryId);
        Assert.Equal("cat-1", force.CatalogueId);
        var unit = Assert.Single(force.Selections);
        Assert.Equal("link-1::se-1", unit.EntryId); // composite id preserved verbatim
        var unitCost = Assert.Single(unit.ObservedCosts);
        Assert.Equal(65.0m, unitCost.Value);
        var child = Assert.Single(unit.Children);
        Assert.Equal("se-2", child.EntryId);
        Assert.Equal(5, child.Count);
        Assert.Equal("Fancy guns", child.CustomName);
    }

    [Fact]
    public void Converts_rosz_zip()
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("test.ros").Open();
            entry.Write(Encoding.UTF8.GetBytes(RosterXml));
        }
        zipStream.Position = 0;

        var roster = RosterFileConverter.Convert(zipStream, "test.rosz");
        Assert.Equal("Test Roster", roster.Name);
    }

    [Fact]
    public void Garbage_input_throws_FormatException()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<not-a-roster/>"));
        Assert.Throws<FormatException>(() => RosterFileConverter.Convert(stream, "x.ros"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter RosterFileConverterTests -v minimal`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/Muster.Cli/Converters/RosterFileConverter.cs
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;
using WarHub.ArmouryModel.Workspaces.BattleScribe;

namespace Muster.Cli.Converters;

/// <summary>
/// Converts a BattleScribe .ros/.rosz roster file into a ReplayRoster.
/// SelectionNode.EntryId is already the composite "linkId::targetId" form the
/// engine's by-id selection expects, so ids pass through verbatim.
/// </summary>
public static class RosterFileConverter
{
    private const int MaxSelections = 5_000;

    public static ReplayRoster Convert(Stream stream, string fileName)
    {
        RosterNode roster;
        try
        {
            roster = fileName.EndsWith(".rosz", StringComparison.OrdinalIgnoreCase)
                ? stream.LoadSourceAuto(fileName) as RosterNode
                    ?? throw new FormatException("archive did not contain a roster")
                : BattleScribeXml.LoadRoster(stream)
                    ?? throw new FormatException("file is not a BattleScribe roster");
        }
        catch (Exception e) when (e is not FormatException)
        {
            throw new FormatException($"could not read roster file: {e.Message}");
        }

        var count = 0;
        var forces = roster.Forces.Select(f => ConvertForce(f, ref count)).ToList();
        if (forces.Count == 0)
            throw new FormatException("roster has no forces");

        return new(
            Name: roster.Name ?? "unnamed roster",
            GameSystemId: roster.GameSystemId,
            ObservedTotals: [.. roster.Costs.Select(c => new ReplayCost(c.Name ?? "", c.TypeId ?? "", c.Value))],
            BooksRevisions: [],
            Forces: forces,
            Unmapped: []);
    }

    private static ReplayForce ConvertForce(ForceNode force, ref int count) => new(
        ForceEntryId: force.EntryId ?? throw new FormatException($"force '{force.Name}' has no entryId"),
        CatalogueId: force.CatalogueId ?? "",
        Selections: ConvertSelections(force.Selections, ref count),
        ChildForces: ConvertForces(force.Forces, ref count));

    private static List<ReplayForce> ConvertForces(IEnumerable<ForceNode> forces, ref int count)
    {
        var result = new List<ReplayForce>();
        foreach (var f in forces) result.Add(ConvertForce(f, ref count));
        return result;
    }

    private static List<ReplaySelection> ConvertSelections(IEnumerable<SelectionNode> selections, ref int count)
    {
        var result = new List<ReplaySelection>();
        foreach (var s in selections)
        {
            if (++count > MaxSelections)
                throw new FormatException($"roster too large (over {MaxSelections} selections)");
            if (s.EntryId is null) continue; // no way to replay — surfaced via structural drift
            result.Add(new(
                EntryId: s.EntryId,
                Count: s.Number,
                CustomName: s.CustomName,
                ObservedCosts: [.. s.Costs.Select(c => new ReplayCost(c.Name ?? "", c.TypeId ?? "", c.Value))],
                Children: ConvertSelections(s.Selections, ref count)));
        }
        return result;
    }
}
```

Note the `ref int count` across lambdas is illegal in C# — use the explicit loop style shown (no LINQ where `ref` is captured); for the top-level `roster.Forces.Select(...)` call, replace with `ConvertForces(roster.Forces, ref count)`. `NodeList<T>` enumerates as `IEnumerable<T>` — verify member access names against the generated node API while implementing (properties confirmed: `RosterNode.Forces/Costs/Name/GameSystemId`, `ForceNode.EntryId/CatalogueId/Selections/Forces`, `SelectionNode.EntryId/Number/CustomName/Costs/Selections`, `CostNode.Name/TypeId/Value`). If a selection with `EntryId == null` is skipped, append a note to `Unmapped` instead of silent skip (spec rule) — adjust the code accordingly: collect notes in a `List<string>` threaded like `count`.

- [ ] **Step 4: Decide per-selection pins (read the comparer first)**

Read `RosterRunner.AssertSelections` (`lib/wham/lib/battlescribe-spec/src/BattleScribeSpec.TestKit/Roster/RosterRunner.cs:757-782`):
- If expected selections match actual by **name** (or entryId): extend `SpecEmitter.Emit` to append, when `pinObserved` and any selection has `ObservedCosts`, a second `expectedState` block with `forces: [ { selections: [ { entryId/name…, costs: […] } ] } ]` pinning per-selection costs. Add an emitter test mirroring `Emitted_yaml_round_trips_through_SpecLoader` asserting the selection-level pin appears and round-trips.
- If matching is **positional/index-based** (fragile against engine auto-adds): do NOT emit per-selection pins; instead add a YAML comment in the emitted spec (`# per-selection costs observed but not pinned (comparer is order-sensitive)`) and record the decision in the commit message.

- [ ] **Step 5: Run tests + Release build**

Run: `dotnet test tests/Muster.Cli.Tests --filter "RosterFileConverterTests|SpecEmitterTests" -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: .ros/.rosz to ReplayRoster converter with per-selection observed costs"
```

---

### Task 8: `muster convert` command

Wires fetcher + converters into the CLI seam the M0–M2 spec named. Input: file path (`.ros`, `.rosz`, `.json`) or NR share URL.

**Files:**
- Create: `src/Muster.Cli/Commands/ConvertCommand.cs`
- Modify: `src/Muster.Cli/Program.cs` (register)
- Test: `tests/Muster.Cli.Tests/ConvertCommandTests.cs`

**Interfaces:**
- Consumes: `NrShareLink`, `NrClient` (Task 5), `NrListParser`, `SpecEmitter` (Task 6), `RosterFileConverter` (Task 7).
- Produces: `muster convert <input> [--id <spec-id>] [--data-source <uri>] [--pin-observed] [-o <file>]`.
  - `<input>` `Argument<string>`: existing file path → dispatch by extension (`.ros`/`.rosz` → RosterFileConverter; `.json` → NrListParser); else if `NrShareLink.TryParse` → fetch via NrClient; else usage error (exit 2).
  - `--id` default: input file name stem or `nr-<key>`.
  - `--data-source` default `"local:."` (documented: replace with the repo's `github:` uri when committing as a fixture).
  - `--pin-observed` default **true** for convert (flag `--no-pin` alternative is NOT added; use `--pin-observed false` — `Option<bool>` with explicit default).
  - Output: YAML to stdout or `-o` file. Exit 0 on success; exit 2 on any failure (fetch error, FormatException) with the human-readable message on stderr — a convert failure is never "fixture failure", so never exit 1.
  - `internal static async Task<int> Run(string input, string? id, string dataSource, bool pinObserved, FileInfo? output)` for testability.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/ConvertCommandTests.cs
using Muster.Cli.Commands;

namespace Muster.Cli.Tests;

public class ConvertCommandTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "nr-list-war-horde.json");

    [Fact]
    public async Task Converts_nr_json_file_to_spec_yaml()
    {
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml"));
        try
        {
            var exit = await ConvertCommand.Run(SamplePath, id: null, dataSource: "local:.", pinObserved: true, output: outFile);

            Assert.Equal(0, exit);
            var yaml = File.ReadAllText(outFile.FullName);
            var spec = BattleScribeSpec.SpecLoader.LoadFromYaml(yaml);
            Assert.Equal("nr-list-war-horde", spec.Id);
            Assert.Contains(spec.Steps, s => s.Action == "addForce");
            Assert.Contains(spec.Steps, s => s.ExpectedState is not null);
        }
        finally { outFile.Delete(); }
    }

    [Fact]
    public async Task Missing_file_exits_2()
    {
        var exit = await ConvertCommand.Run("no-such-file.ros", null, "local:.", true, null);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Non_allowlisted_url_exits_2()
    {
        var exit = await ConvertCommand.Run("https://evil.example/app/list/x", null, "local:.", true, null);
        Assert.Equal(2, exit);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter ConvertCommandTests -v minimal`
Expected: FAIL.

- [ ] **Step 3: Implement**

`ConvertCommand.Create()` mirrors the existing command style (`TestCommand.cs`). Core of `Run`:

```csharp
internal static async Task<int> Run(string input, string? id, string dataSource, bool pinObserved, FileInfo? output)
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
                roster = NrListParser.Parse(await File.ReadAllTextAsync(input));
            }
            else if (ext.Equals(".ros", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rosz", StringComparison.OrdinalIgnoreCase))
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
            var fetched = await client.FetchListAsync(key);
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
        if (output is null) Console.Out.Write(yaml);
        else await File.WriteAllTextAsync(output.FullName, yaml);
        return 0;
    }
    catch (FormatException e)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
}
```

Register in `Program.CreateRootCommand`: `root.Subcommands.Add(ConvertCommand.Create());`. System.CommandLine async action: `command.SetAction((parse, ct) => Run(...))` — follow the async overload pattern; check how `InvokeAsync` maps `Task<int>` (existing commands are sync — mirror `TestCommand` but with the async `SetAction` overload, which 2.0.3 supports).

- [ ] **Step 4: Run the full suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: PASS (includes `ProgramExitCodeTests` — `convert` with no args must exit 2 via parse-error remap).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: muster convert — roster file or NR link to fixture-DSL spec"
```

---

### Task 9: Issue body parsing + attachment fetching

Extract the roster source and reporter text from a hostile issue body: muster's own form layout, NR's auto-report layout, attachment URLs, inline YAML blocks.

**Files:**
- Create: `src/Muster.Cli/Reports/IssueBody.cs`
- Create: `src/Muster.Cli/Reports/AttachmentClient.cs`
- Test: `tests/Muster.Cli.Tests/Reports/IssueBodyTests.cs`, `tests/Muster.Cli.Tests/Reports/AttachmentClientTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RosterSource(RosterSourceKind Kind, string Value)`; `enum RosterSourceKind { NrLink, Attachment, InlineYaml }`
  - `sealed record IssueBody(RosterSource? Roster, string? Problem, string? Expected)`; `static IssueBody Parse(string body)`:
    - NR link: first match of the `NrShareLink` pattern anywhere in the body (covers both muster's form and NR auto-reports' `**List:** <url>` line) → `NrLink` with the URL.
    - Attachment: first URL matching `^https://github\.com/user-attachments/files/\d+/[A-Za-z0-9._-]+\.(ros|rosz|zip)$` (also accept legacy `https://github.com/<owner>/<repo>/files/\d+/…`) → `Attachment`.
    - Inline YAML: first fenced code block ` ```yaml … ``` ` whose content contains `steps:` → `InlineYaml` with the block content.
    - Precedence: NrLink > Attachment > InlineYaml (a body with several candidates uses the first by that ranking). No match → `Roster = null`.
    - `Problem`/`Expected`: text following `**Problem:**` / `**Expected:**` markers up to the next `**` marker or blank-line paragraph break (both muster's form and NR auto-reports use these); null when absent. Body size cap: parse at most the first 65 536 characters.
  - `sealed class AttachmentClient(HttpMessageHandler? handler = null) : IDisposable`: `Task<(byte[]? Data, string? Error)> DownloadAsync(string url, CancellationToken ct = default)` — re-validates the URL against the attachment pattern (defense in depth), 30 s timeout, 5 MB cap, follows redirects (GitHub attachment URLs redirect to storage), same graceful-error contract as `NrClient`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/Reports/IssueBodyTests.cs
using Muster.Cli.Reports;

namespace Muster.Cli.Tests.Reports;

public class IssueBodyTests
{
    [Fact]
    public void Parses_nr_auto_report()
    {
        // Verbatim shape of New Recruit auto-filed issues (BSData/wh40k-11e#234)
        var body = """
            **Problem:**
            Outriders Squad 6 members - 145 points

            **Expected:**
            Outriders Squad 6 members - 140 points

            **List:** https://www.newrecruit.eu/app/list/tr5BL
            **NewRecruit Version:** 34.99
            """;
        var parsed = IssueBody.Parse(body);

        Assert.Equal(RosterSourceKind.NrLink, parsed.Roster!.Kind);
        Assert.Equal("https://www.newrecruit.eu/app/list/tr5BL", parsed.Roster.Value);
        Assert.Contains("145 points", parsed.Problem, StringComparison.Ordinal);
        Assert.Contains("140 points", parsed.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_attachment_url()
    {
        var body = "My roster: https://github.com/user-attachments/files/12345/my-list.rosz broken!";
        var parsed = IssueBody.Parse(body);
        Assert.Equal(RosterSourceKind.Attachment, parsed.Roster!.Kind);
        Assert.EndsWith("my-list.rosz", parsed.Roster.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_inline_yaml_block()
    {
        var body = """
            Points look wrong:

            ```yaml
            steps:
              - action: addForce
                forceEntryId: "fe-1"
            ```
            """;
        var parsed = IssueBody.Parse(body);
        Assert.Equal(RosterSourceKind.InlineYaml, parsed.Roster!.Kind);
        Assert.Contains("addForce", parsed.Roster.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Nr_link_wins_over_attachment_and_yaml()
    {
        var body = """
            https://github.com/user-attachments/files/1/x.rosz
            **List:** https://www.newrecruit.eu/app/list/abc12
            ```yaml
            steps: []
            ```
            """;
        Assert.Equal(RosterSourceKind.NrLink, IssueBody.Parse(body).Roster!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just words, no roster anywhere")]
    [InlineData("https://evil.example/app/list/abc12")]
    [InlineData("```yaml\nname: no steps here\n```")]
    public void No_roster_source_yields_null(string body) =>
        Assert.Null(IssueBody.Parse(body).Roster);

    [Fact]
    public void Huge_body_is_truncated_not_hung()
    {
        var body = new string('a', 10_000_000) + " **List:** https://www.newrecruit.eu/app/list/abc12";
        var parsed = IssueBody.Parse(body); // must return fast
        Assert.Null(parsed.Roster); // link is beyond the 64 KiB parse window
    }
}
```

`AttachmentClientTests`: mirror `NrClientTests` structure — stub handler; assert URL re-validation rejects `https://evil.example/x.rosz` without any HTTP call (`StubHandler` asserts it was never invoked); assert 5 MB cap; assert success path returns bytes.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter "IssueBodyTests|AttachmentClientTests" -v minimal`
Expected: FAIL.

- [ ] **Step 3: Implement**

`IssueBody.Parse` outline (implement with `GeneratedRegex`, all patterns anchored/bounded, input truncated to 65 536 chars first):

```csharp
[GeneratedRegex(@"https://www\.newrecruit\.eu/app/list/[A-Za-z0-9]{1,32}")]
private static partial Regex NrLinkPattern();

[GeneratedRegex(@"https://github\.com/(?:user-attachments/files|[\w.-]+/[\w.-]+/files)/\d+/[A-Za-z0-9._-]+\.(?:ros|rosz|zip)")]
private static partial Regex AttachmentPattern();

[GeneratedRegex(@"```ya?ml\s*\n(.*?)```", RegexOptions.Singleline)]
private static partial Regex YamlBlockPattern();

[GeneratedRegex(@"\*\*Problem:\*\*\s*\n?(.*?)(?=\n\s*\*\*|\z)", RegexOptions.Singleline)]
private static partial Regex ProblemPattern();

[GeneratedRegex(@"\*\*Expected:\*\*\s*\n?(.*?)(?=\n\s*\*\*|\z)", RegexOptions.Singleline)]
private static partial Regex ExpectedPattern();
```

Precedence logic: check NR link first, then attachment, then the first yaml block containing `steps:`. Trim captured Problem/Expected to 2 000 chars.

`AttachmentClient`: copy the `NrClient` streaming-cap body; `HttpClientHandler { AllowAutoRedirect = true }`; re-validate the URL with `AttachmentPattern` before any request.

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Muster.Cli.Tests --filter "IssueBodyTests|AttachmentClientTests" -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Muster.Cli/Reports tests/Muster.Cli.Tests/Reports
git commit -m "feat: hostile-input issue body parser + allowlisted attachment client"
```

---

### Task 10: Verdict mapping + reply rendering + `muster report`

The report pipeline: body → roster → spec → per-engine evaluation → verdict + labels + markdown reply with per-engine matrix and `<!-- muster:snapshot -->` details block.

**Files:**
- Create: `src/Muster.Cli/Reports/Verdict.cs`
- Create: `src/Muster.Cli/Reports/ReplyRenderer.cs`
- Create: `src/Muster.Cli/Commands/ReportCommand.cs`
- Modify: `src/Muster.Cli/Program.cs` (register)
- Test: `tests/Muster.Cli.Tests/Reports/VerdictTests.cs`, `tests/Muster.Cli.Tests/ReportCommandTests.cs`

**Interfaces:**
- Consumes: `IssueBody` (Task 9), `NrClient`/`NrShareLink` (5), `NrListParser`/`SpecEmitter`/`ReplayRoster` (6), `RosterFileConverter` (7), `MultiRunReport` machinery (3) — report runs the ONE generated spec by writing it to a temp fixtures dir and calling `MultiRunReport.Run(dataDir, tempFixturesDir, engines, governing)`.
- Produces:
  - `enum VerdictKind { Confirmed, NotReproducible, NeedsInfo, Inconclusive }`
  - `sealed record Verdict(VerdictKind Kind, bool EngineGap, IReadOnlyList<string> Labels, string? Detail)`
  - `static class VerdictMapper`: `Verdict Map(ReplayRoster? roster, string? conversionError, MultiRunReport? runs)`:
    1. `conversionError != null` OR `roster == null` OR `roster.Unmapped.Count > 0` → `NeedsInfo` (detail = error / unmapped notes).
    2. `runs` has no successful runs (all unavailable) → `Inconclusive`.
    3. Governing engine's single fixture result: `HarnessError`/inconclusive → `NeedsInfo` (detail: "the roster could not be replayed against current data" + first failure line) — replay failures are NOT "not-reproducible".
    4. Governing passed (pins matched) → `Confirmed`.
    5. Governing failed assertions → `NotReproducible` (detail = failure lines, which carry expected-vs-actual).
    6. `EngineGap = true` when ≥2 engines ran and their fixture pass/fail status differs; adds `engine-gap` to `Labels`.
    - `Labels` always includes the kind's label (`confirmed`, `not-reproducible`, `needs-info`, `inconclusive`).
  - `static class ReplyRenderer`: `string Render(Verdict verdict, ReplayRoster? roster, MultiRunReport? runs, string specYaml, string? problem, string? expected)` — markdown reply:
    - first line: `<!-- muster:report -->` (sticky-comment marker)
    - verdict heading + one-paragraph explanation (which engine governed; engines that ran/unavailable)
    - value × engine matrix: rows = pinned observed values (name + observed value from `roster.ObservedTotals`), columns = `reported` + each engine that ran (engine cell: `reproduced ✔` when that engine passed pins / actual value parsed from its failure line when not — if the actual value can't be parsed from the failure string, render `differs ✖`)
    - reporter's Problem/Expected quoted back (truncated 500 chars each)
    - `books_revision` note: "reported against: Xenos - Orks: 2 — evaluated against current data"
    - collapsed snapshot: `<details><summary>Executable spec (snapshot)</summary>` + `<!-- muster:snapshot -->` + fenced yaml block with `specYaml` + `</details>`
    - needs-info variant: polite ask naming exactly what was missing/invalid, plus the three accepted roster formats.
  - `ReportCommand`: `muster report --issue-body <file> --data <root> [--engines …] [--governing …] [--out-dir <dir>]` — writes `reply.md`, `report.json` (`{verdict, labels, engineGap, governing}` camelCase), `snapshot.yaml` into `--out-dir` (default `.`); prints reply to stdout too. Exit 0 whenever a reply was produced (ALL verdicts including needs-info); exit 2 only on harness errors (missing --data dir, unwritable out-dir).
  - Data-source rule: the generated spec's `dataSource` must match what the data repo's fixtures use so the hermeticity gate passes — `ReportCommand` takes `--data-source <uri>` (default: detect from an existing fixture in `--fixtures`? NO — explicit option, default `local:.`; entrypoint wires the real value in Task 12).

- [ ] **Step 1: Write the failing verdict tests**

```csharp
// tests/Muster.Cli.Tests/Reports/VerdictTests.cs
using Muster.Cli.Reports;
using Muster.Cli.Reporting;

namespace Muster.Cli.Tests.Reports;

public class VerdictTests
{
    private static ReplayRoster Roster(params string[] unmapped) => new(
        "r", "gs", [new("pts", "pt-1", 100m)], [], 
        [new("fe-1", "cat-1", [], [])], [.. unmapped]);

    private static MultiRunReport Runs(params (string Engine, bool Passed, bool Inconclusive)[] engines) => new(
        Governing: engines.Length > 0 ? engines[0].Engine : null,
        Unavailable: [],
        Runs: [.. engines.Select(e => RunReport.Create(e.Engine, "data",
            [new FixtureResult("spec", "p", e.Passed, e.Passed ? [] : ["Step 3: cost pts: expected 100 but got 95"], 1, e.Inconclusive)]))]);

    [Fact]
    public void Conversion_error_is_needs_info()
    {
        var v = VerdictMapper.Map(null, "list not found", null);
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
        Assert.Contains("needs-info", v.Labels);
    }

    [Fact]
    public void Unmapped_nodes_force_needs_info()
    {
        var v = VerdictMapper.Map(Roster("selection 'X' missing option_id"), null, Runs(("wham", true, false)));
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
    }

    [Fact]
    public void Governing_pass_is_confirmed()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", true, false)));
        Assert.Equal(VerdictKind.Confirmed, v.Kind);
        Assert.Contains("confirmed", v.Labels);
        Assert.False(v.EngineGap);
    }

    [Fact]
    public void Governing_assertion_failure_is_not_reproducible()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", false, false)));
        Assert.Equal(VerdictKind.NotReproducible, v.Kind);
        Assert.Contains("expected 100 but got 95", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_crash_is_needs_info_not_notreproducible()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", false, true)));
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
    }

    [Fact]
    public void Engine_disagreement_raises_engine_gap()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("newrecruit", true, false), ("wham", false, false)));
        Assert.Equal(VerdictKind.Confirmed, v.Kind); // newrecruit governs
        Assert.True(v.EngineGap);
        Assert.Contains("engine-gap", v.Labels);
    }

    [Fact]
    public void No_engines_ran_is_inconclusive()
    {
        var runs = new MultiRunReport(null, ["newrecruit"], []);
        var v = VerdictMapper.Map(Roster(), null, runs);
        Assert.Equal(VerdictKind.Inconclusive, v.Kind);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter VerdictTests -v minimal`
Expected: FAIL.

- [ ] **Step 3: Implement Verdict + VerdictMapper + ReplyRenderer**

`Verdict.cs`:

```csharp
// src/Muster.Cli/Reports/Verdict.cs
using Muster.Cli.Converters;
using Muster.Cli.Reporting;

namespace Muster.Cli.Reports;

public enum VerdictKind { Confirmed, NotReproducible, NeedsInfo, Inconclusive }

public sealed record Verdict(VerdictKind Kind, bool EngineGap, IReadOnlyList<string> Labels, string? Detail);

public static class VerdictMapper
{
    public static Verdict Map(ReplayRoster? roster, string? conversionError, MultiRunReport? runs)
    {
        if (conversionError is not null || roster is null)
            return Make(VerdictKind.NeedsInfo, false, conversionError ?? "no roster found in the report");
        if (roster.Unmapped.Count > 0)
            return Make(VerdictKind.NeedsInfo, false, string.Join("\n", roster.Unmapped));
        if (runs is null || runs.Runs.Count == 0 || runs.Governing is null)
            return Make(VerdictKind.Inconclusive, false, "no engine was available to evaluate the roster");

        var governing = runs.Runs.First(r => r.Engine == runs.Governing);
        var fixture = governing.Fixtures[0];

        var gap = runs.Runs.Count > 1 && runs.Runs
            .Select(r => (r.Fixtures[0].Passed, r.Fixtures[0].Inconclusive))
            .Distinct().Count() > 1;

        if (fixture.Inconclusive)
            return Make(VerdictKind.NeedsInfo, gap,
                "the roster could not be replayed against current data: " + fixture.Failures.FirstOrDefault());
        return fixture.Passed
            ? Make(VerdictKind.Confirmed, gap, null)
            : Make(VerdictKind.NotReproducible, gap, string.Join("\n", fixture.Failures));
    }

    private static Verdict Make(VerdictKind kind, bool gap, string? detail)
    {
        var labels = new List<string>
        {
            kind switch
            {
                VerdictKind.Confirmed => "confirmed",
                VerdictKind.NotReproducible => "not-reproducible",
                VerdictKind.NeedsInfo => "needs-info",
                _ => "inconclusive",
            },
        };
        if (gap) labels.Add("engine-gap");
        return new(kind, gap, labels, detail);
    }
}
```

`ReplyRenderer.Render`: build the markdown per the interface contract above (plain `StringBuilder`; escape `|` in user text with `\|`; wrap Problem/Expected in `> ` blockquotes). Write focused tests inline in `ReportCommandTests` (marker presence, matrix row for the pinned pts value, snapshot block contains the YAML, needs-info variant names the reason).

`ReportCommand.Run` outline:

```csharp
internal static async Task<int> Run(FileInfo issueBody, DirectoryInfo data, string dataSource,
    string[] engines, string[] governing, DirectoryInfo? outDir)
{
    // harness-level validation → exit 2
    if (!issueBody.Exists || !data.Exists) { Console.Error.WriteLine("…"); return 2; }
    var body = IssueBody.Parse(await File.ReadAllTextAsync(issueBody.FullName));

    ReplayRoster? roster = null;
    string? specYaml = null;
    string? error = null;
    switch (body.Roster)
    {
        case null:
            error = "no roster found: accepted formats are a New Recruit share link, a .ros/.rosz attachment, or an inline ```yaml steps block";
            break;
        case { Kind: RosterSourceKind.NrLink } src:
            // NrShareLink.TryParse (already matched), NrClient fetch, NrListParser.Parse — FormatException → error
            break;
        case { Kind: RosterSourceKind.Attachment } src:
            // AttachmentClient download → RosterFileConverter.Convert
            break;
        case { Kind: RosterSourceKind.InlineYaml } src:
            // inline: the YAML *is* the spec — validate via SpecLoader.LoadFromYaml (catch → error),
            // specYaml = src.Value; roster stays null but conversion is fine:
            // handle via a separate bool inlineMode — VerdictMapper needs runs only.
            break;
    }
    if (roster is not null)
        specYaml = SpecEmitter.Emit(roster, specId: "report", dataSource, pinObserved: true);

    MultiRunReport? runs = null;
    if (specYaml is not null && error is null)
    {
        var tempFixtures = Directory.CreateTempSubdirectory("muster-report-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempFixtures.FullName, "report.yaml"), specYaml);
            runs = MultiRunReport.Run(data.FullName, tempFixtures.FullName, engines, governing);
        }
        finally { tempFixtures.Delete(recursive: true); }
    }

    var verdict = VerdictMapper.Map(roster, error, runs);
    // inlineMode adjustment: when inline spec ran, roster is null but error is null too —
    // pass a minimal ReplayRoster (name from spec id, no pins) so Map doesn't call it needs-info.
    var reply = ReplyRenderer.Render(verdict, roster, runs, specYaml ?? "", body.Problem, body.Expected);

    var dir = outDir?.FullName ?? ".";
    await File.WriteAllTextAsync(Path.Combine(dir, "reply.md"), reply);
    await File.WriteAllTextAsync(Path.Combine(dir, "report.json"), ToJson(verdict, runs));
    if (specYaml is not null) await File.WriteAllTextAsync(Path.Combine(dir, "snapshot.yaml"), specYaml);
    Console.Out.WriteLine(reply);
    return 0;
}
```

Resolve the `inlineMode` wrinkle cleanly: change `VerdictMapper.Map`'s first parameter to `bool hasRoster, IReadOnlyList<string> unmapped` OR overload for inline mode — implementer's choice, tests pin the behavior: inline spec + passing run = `Confirmed`.

`ReportCommandTests`: end-to-end with `TestRepoFactory` data + an inline-yaml issue body (reuses the green fixture's YAML with pins matching → Confirmed; with pins mismatching → NotReproducible; garbage body → NeedsInfo with exit 0; missing data dir → exit 2). Assert `reply.md` contains `<!-- muster:report -->` and `snapshot.yaml` written.

- [ ] **Step 4: Run the full suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: muster report — issue body to verdict, labels, per-engine reply, snapshot"
```

---

### Task 11: `muster promote`

Extract the newest `<!-- muster:snapshot -->` block from the harness's own issue comments, strip observed pins, re-run capturing final state via `RosterRunner.OnStepCompleted`, re-pin to current values, write `tests/rosters/<slug>.yaml`.

**Files:**
- Create: `src/Muster.Cli/Reports/SnapshotExtractor.cs`
- Create: `src/Muster.Cli/Reports/SpecRePinner.cs`
- Create: `src/Muster.Cli/Commands/PromoteCommand.cs`
- Modify: `src/Muster.Cli/Program.cs` (register)
- Test: `tests/Muster.Cli.Tests/Reports/SnapshotExtractorTests.cs`, `tests/Muster.Cli.Tests/PromoteCommandTests.cs`

**Interfaces:**
- Consumes: `SpecLoader.LoadFromYaml`, `RosterRunner` + `OnStepCompleted` callback (signature `Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>`), `EngineRegistry` (Task 2), `RepoDataSourceResolver`.
- Produces:
  - `static class SnapshotExtractor`: `string? ExtractLatest(string commentsJson)` — input is `gh api /repos/{o}/{r}/issues/{n}/comments` output (JSON array of objects with `body`); returns the fenced-yaml content of the LAST comment containing `<!-- muster:snapshot -->`, or null. Parse with System.Text.Json + regex for the fenced block; body cap 256 KiB per comment.
  - `static class SpecRePinner`: `string RePin(string specYaml, string dataDir, EngineSpec engine, string newSpecId)` — loads the spec, removes all `expectedState`-only steps, runs the remaining steps via `RosterRunner` (with `RepoDataSourceResolver.Create(dataDir)` + `IsPopulatedFor` gate → throws `HarnessInconclusiveException` if unpopulated), captures the final `RosterState` from `OnStepCompleted`, and re-emits the spec (via a small extension to `SpecEmitter` or direct text surgery on the step list: keep the original step lines verbatim, replace the trailing expectedState block) with `expectedState.costs` pinned to the captured state's `Costs` and the new id. Throws `InvalidOperationException` when the run itself fails (a fixture that can't run can't be promoted).
  - `PromoteCommand`: `muster promote --issue-body <file> --comments <file> --data <root> --issue-number <n> [--engines …] [--governing …] [--out <dir>]` — out default `tests/rosters`; slug = kebab-cased issue title from `--issue-body` first line if it looks like a title file? NO — title is not in the body; slug = `report-issue-<n>`; collision → `report-issue-<n>-2`, `-3`, …. Prints the written file path on stdout. Exit 0 written / 2 anything else.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Muster.Cli.Tests/Reports/SnapshotExtractorTests.cs
using Muster.Cli.Reports;

namespace Muster.Cli.Tests.Reports;

public class SnapshotExtractorTests
{
    [Fact]
    public void Extracts_yaml_from_latest_snapshot_comment()
    {
        var comments = """
            [
              {"body": "just a human comment"},
              {"body": "<!-- muster:report -->\nold reply\n<details><summary>Executable spec (snapshot)</summary>\n<!-- muster:snapshot -->\n\n```yaml\nid: \"old\"\n```\n\n</details>"},
              {"body": "<!-- muster:report -->\nnew reply\n<details><summary>Executable spec (snapshot)</summary>\n<!-- muster:snapshot -->\n\n```yaml\nid: \"new\"\nsteps: []\n```\n\n</details>"}
            ]
            """;
        var yaml = SnapshotExtractor.ExtractLatest(comments);
        Assert.NotNull(yaml);
        Assert.Contains("id: \"new\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("old", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"body": "no snapshot here"}]""")]
    [InlineData("not json")]
    public void Missing_snapshot_returns_null(string comments) =>
        Assert.Null(SnapshotExtractor.ExtractLatest(comments));
}
```

`PromoteCommandTests` (uses `TestRepoFactory` + the builtin wham engine over the existing green test-repo data):
- comments file containing a snapshot whose steps replay against the test repo (reuse the green fixture's steps with WRONG pinned costs, e.g. `value: 999`) → promote writes `tests/rosters/report-issue-7.yaml` whose `expectedState.costs` pin the CURRENT engine value (20), not 999 — assert by `SpecLoader.Load` on the written file.
- name collision: pre-create `report-issue-7.yaml` → new file is `report-issue-7-2.yaml`.
- comments without snapshot → exit 2, stderr message.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Muster.Cli.Tests --filter "SnapshotExtractorTests|PromoteCommandTests" -v minimal
Expected: FAIL.

- [ ] **Step 3: Implement**

`SnapshotExtractor`: JSON-parse the array defensively (bad JSON → null); iterate in order keeping the last match of

```csharp
[GeneratedRegex(@"<!-- muster:snapshot -->\s*```ya?ml\s*\n(.*?)```", RegexOptions.Singleline)]
```

`SpecRePinner` core:

```csharp
public static string RePin(string specYaml, string dataDir, EngineSpec engineSpec, string newSpecId)
{
    var spec = SpecLoader.LoadFromYaml(specYaml, defaultId: newSpecId);
    if (spec.Setup.DataSource is { Length: > 0 } ds && !RepoDataSourceResolver.IsPopulatedFor(dataDir, ds))
        throw new InvalidOperationException($"data source not populated locally: {ds}");

    // strip assertion-only steps (they may pin stale/buggy values)
    var replaySteps = spec.Steps.Where(s => s.Action is not null).ToList();
    var replaySpec = /* SpecFile with Steps = replaySteps — records: spec with { Steps = replaySteps }
                        (check SpecFile is a record / has settable Steps; if init-only list,
                        construct a new SpecFile copying Id/Category/Description/Setup) */;

    RosterState? finalState = null;
    using var engine = EngineRegistry.CreateEngine(engineSpec);
    var runner = new RosterRunner(engine, RepoDataSourceResolver.Create(dataDir), engineName: engineSpec.Name)
    {
        OnStepCompleted = (_, _, state, _) => finalState = state,
    };
    var result = runner.Run(replaySpec);
    if (result.HarnessError is not null || !result.Passed || finalState is null)
        throw new InvalidOperationException(
            "cannot promote: replay failed against current data: "
            + (result.HarnessError ?? result.Failures.FirstOrDefault() ?? "no state captured"));

    // re-emit: original replay step YAML text preserved, new trailing pin block
    return RewriteWithPins(specYaml, newSpecId, finalState.Costs);
}
```

`RewriteWithPins`: text-level — replace the `id:` line value with `newSpecId`; drop any existing `- expectedState:`-only trailing block (recognize by the emitted comment marker `# observed values from the report` OR by locating steps that have `expectedState` and no `action` — since SpecEmitter emits pins as the final step, dropping from the marker line to EOF is deterministic for muster-emitted snapshots; fall back to append-only when the marker is absent); append a fresh pin block with the captured costs and comment `# expected values pinned at promotion (engine: <name>)`. Validate output with `SpecLoader.LoadFromYaml` before returning (throw otherwise).

`PromoteCommand.Run`: read comments file → `ExtractLatest` (null → exit 2) → slug/collision loop → `SpecRePinner.RePin` (catch `InvalidOperationException`/`HarnessInconclusiveException` → stderr + exit 2) → write file, print path, exit 0.

- [ ] **Step 4: Run the full suite + Release build**

Run: `dotnet test -v minimal && dotnet build -c Release`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: muster promote — snapshot extraction, re-pin to current values, fixture file"
```

---

### Task 12: GitHub kit — issue form, reusable report workflow, promote flow, docs

Everything a data repo installs. Lives in WarHub/muster; the pilot fork copies the caller stubs.

**Files:**
- Create: `kit/issue-form/report-a-data-bug.yml` (template data repos copy to `.github/ISSUE_TEMPLATE/`)
- Create: `.github/workflows/report-check.yml` (reusable, `workflow_call`)
- Create: `kit/callers/muster-report.yml` (thin caller data repos copy to `.github/workflows/`)
- Modify: `entrypoint.sh` (report/promote modes)
- Modify: `docs/authoring-fixtures.md` (link) + Create: `docs/executable-bug-reports.md`
- Test: manual `act`-free validation — `python -c "import yaml,sys; yaml.safe_load(open(sys.argv[1]))"` per file + entrypoint bash gates below

**Interfaces:**
- Consumes: `muster report` (Task 10), `muster promote` (Task 11) via the container image; sticky comments via `peter-evans/find-comment@v3` + `peter-evans/create-or-update-comment@v4` keyed on `<!-- muster:report -->`; labels + PR via `gh` CLI with `GITHUB_TOKEN`.
- Produces: a data repo needs exactly two files (issue template + caller workflow) and the labels get created idempotently on first run.

- [ ] **Step 1: Issue form template**

```yaml
# kit/issue-form/report-a-data-bug.yml
name: Report a data bug
description: Report wrong points, missing options, or broken constraints. Your roster makes the bug reproducible.
title: "[data bug]: "
labels: []
body:
  - type: markdown
    attributes:
      value: |
        Muster will automatically evaluate your roster against the latest data
        and reply with what the engines actually compute.
  - type: input
    id: roster
    attributes:
      label: Roster
      description: >
        A New Recruit share link (https://www.newrecruit.eu/app/list/…), OR drag
        a .rosz file into the field below, OR paste fixture YAML in the details field.
      placeholder: "https://www.newrecruit.eu/app/list/abc12"
    validations:
      required: false
  - type: textarea
    id: problem
    attributes:
      label: Problem
      description: What does the app show? (Muster echoes this back — markers must stay)
      placeholder: "Outriders Squad 6 members - 145 points"
    validations:
      required: true
  - type: textarea
    id: expected
    attributes:
      label: Expected
      description: What should it show, per the printed rules?
      placeholder: "Outriders Squad 6 members - 140 points"
    validations:
      required: true
  - type: textarea
    id: details
    attributes:
      label: Attachments / inline spec
      description: "Drop a .rosz here (GitHub may require zipping it), or paste a ```yaml steps block."
    validations:
      required: false
```

GitHub renders form fields into the body as `### Roster\n\n<value>\n\n### Problem\n\n<value>` — **verify `IssueBody.Parse` handles the `### Problem` heading form too**; if it only matches `**Problem:**`, extend the Task 9 regexes with the alternative `(?:\*\*Problem:\*\*|### Problem)` and add a form-shaped test case (do this as part of this task; it is one regex + one test).

- [ ] **Step 2: Reusable workflow**

```yaml
# .github/workflows/report-check.yml
name: Muster report check
on:
  workflow_call:
    inputs:
      data-path:
        type: string
        default: "."
      data-source:
        type: string
        description: dataSource URI matching the repo's fixtures (e.g. github:BSData/wh40k-11e)
        required: true
      engines:
        type: string
        default: "wham"
      governing:
        type: string
        default: "newrecruit battlescribe wham"
      fixtures-out:
        type: string
        default: "tests/rosters"

permissions:
  issues: write
  contents: write
  pull-requests: write

jobs:
  evaluate:
    if: >
      github.event_name == 'issues' ||
      (github.event_name == 'issue_comment' && contains(github.event.comment.body, '/muster check'))
    runs-on: ubuntu-latest
    container: ghcr.io/warhub/muster:latest
    steps:
      - uses: actions/checkout@v4
      - name: Save issue body
        env:
          BODY: ${{ github.event.issue.body }}
        run: printf '%s' "$BODY" > /tmp/issue-body.md
      - name: Evaluate report
        run: |
          /entrypoint.sh report "${{ inputs.data-path }}" /tmp/issue-body.md \
            "${{ inputs.data-source }}" "${{ inputs.engines }}" "${{ inputs.governing }}"
      - name: Find previous reply
        uses: peter-evans/find-comment@v3
        id: fc
        with:
          issue-number: ${{ github.event.issue.number }}
          body-includes: "<!-- muster:report -->"
      - name: Post reply
        uses: peter-evans/create-or-update-comment@v4
        with:
          comment-id: ${{ steps.fc.outputs.comment-id }}
          issue-number: ${{ github.event.issue.number }}
          body-path: reply.md
          edit-mode: replace
      - name: Apply labels
        env:
          GH_TOKEN: ${{ github.token }}
          ISSUE: ${{ github.event.issue.number }}
        run: |
          for label in confirmed not-reproducible needs-info inconclusive engine-gap; do
            gh label create "$label" --force --color "$(case $label in
              confirmed) echo d73a4a;; not-reproducible) echo 0e8a16;;
              needs-info) echo fbca04;; inconclusive) echo d4c5f9;;
              engine-gap) echo 5319e7;; esac)" -R "$GITHUB_REPOSITORY" || true
          done
          labels=$(python3 -c "import json;print(','.join(json.load(open('report.json'))['labels']))")
          current=$(gh issue view "$ISSUE" -R "$GITHUB_REPOSITORY" --json labels -q '[.labels[].name] | map(select(. == "confirmed" or . == "not-reproducible" or . == "needs-info" or . == "inconclusive" or . == "engine-gap")) | join(",")')
          [ -n "$current" ] && gh issue edit "$ISSUE" -R "$GITHUB_REPOSITORY" --remove-label "$current" || true
          gh issue edit "$ISSUE" -R "$GITHUB_REPOSITORY" --add-label "$labels"

  promote:
    if: github.event_name == 'issue_comment' && contains(github.event.comment.body, '/muster promote')
    runs-on: ubuntu-latest
    container: ghcr.io/warhub/muster:latest
    steps:
      - name: Check commenter permission
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          perm=$(gh api "repos/$GITHUB_REPOSITORY/collaborators/${{ github.event.comment.user.login }}/permission" -q .permission)
          case "$perm" in admin|write|maintain) ;; *)
            gh issue comment "${{ github.event.issue.number }}" -R "$GITHUB_REPOSITORY" \
              -b "Sorry @${{ github.event.comment.user.login }}, promotion needs write access."
            exit 1;; esac
      - uses: actions/checkout@v4
      - name: Fetch comments and issue body
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          gh api "repos/$GITHUB_REPOSITORY/issues/${{ github.event.issue.number }}/comments" --paginate > /tmp/comments.json
          gh api "repos/$GITHUB_REPOSITORY/issues/${{ github.event.issue.number }}" -q .body > /tmp/issue-body.md
      - name: Promote
        run: |
          /entrypoint.sh promote "${{ inputs.data-path }}" /tmp/issue-body.md /tmp/comments.json \
            "${{ github.event.issue.number }}" "${{ inputs.fixtures-out }}" \
            "${{ inputs.engines }}" "${{ inputs.governing }}"
      - name: Open PR
        env:
          GH_TOKEN: ${{ github.token }}
          ISSUE: ${{ github.event.issue.number }}
        run: |
          git config user.name "muster-bot"
          git config user.email "muster@users.noreply.github.com"
          branch="muster/promote-issue-$ISSUE"
          git checkout -b "$branch"
          git add "${{ inputs.fixtures-out }}"
          git commit -m "test: promote issue #$ISSUE roster to golden fixture"
          git push origin "$branch"
          gh pr create -R "$GITHUB_REPOSITORY" --head "$branch" \
            --title "Promote issue #$ISSUE roster to golden fixture" \
            --body "Promotes the reproducing roster from #$ISSUE to tests/rosters/. Review the pinned expected values — they were captured from the current (post-fix) engine evaluation. Closes #$ISSUE."
```

- [ ] **Step 3: Caller stub + entrypoint modes**

```yaml
# kit/callers/muster-report.yml — data repos copy to .github/workflows/
name: Muster bug reports
on:
  issues:
    types: [opened, edited]
  issue_comment:
    types: [created]
jobs:
  report:
    uses: WarHub/muster/.github/workflows/report-check.yml@main
    with:
      data-source: "github:BSData/wh40k-11e"   # ← repo-specific
```

`entrypoint.sh`: prepend mode dispatch — first arg `report` / `promote` selects new flows; anything else falls through to the existing test/diff flow (backward compatible: the Action passes data-path first, never a mode word):

```bash
MODE="$1"
if [[ "$MODE" == "report" ]]; then
  shift
  DATA_PATH="$1"; BODY_FILE="$2"; DATA_SOURCE="$3"; ENGINES_INPUT="$4"; GOVERNING_INPUT="$5"
  # build dataroot exactly like test mode, but seeded from DATA_SOURCE instead of
  # scanning fixtures (extract org/repo/ref → same cache layout)
  # then: muster report --issue-body "$BODY_FILE" --data "$DATAROOT" \
  #         --data-source "$DATA_SOURCE" --engines … --governing … --out-dir .
  # exit code passthrough: report exits 0 for all verdicts; 2 → ::error:: + exit 1
  # (a report harness error must be visible, not neutral)
  …
elif [[ "$MODE" == "promote" ]]; then
  shift
  # analogous: muster promote --issue-body … --comments … --data "$DATAROOT" --issue-number … --out …
  …
fi
```

Write the two blocks fully in the implementation (mirror `build_dataroot`'s cache-layout logic with the dataSource string as the seed — factor the layout code into a `build_dataroot_for_source()` function reused by both paths).

- [ ] **Step 4: Validate + docs + commit**

- YAML-validate all three workflow/template files (`python -c "import yaml; yaml.safe_load(open('…'))"`).
- Bash-gate entrypoint: `bash -n entrypoint.sh` plus a dry-run of `report` mode against the test repo fixture data with `MUSTER_CMD` pointing at a local `dotnet publish` output (same gate style as M0–M2 Task 11).
- `docs/executable-bug-reports.md`: install steps (2 files), the three roster formats, verdict/label semantics table, `/muster check` + `/muster promote` commands, engines/governing configuration, the NR-adapter docker registration for repos that want the default governor.

```bash
git add kit .github/workflows/report-check.yml entrypoint.sh docs
git commit -m "feat: issue-form kit + reusable report/promote workflows + docs"
```

---

### Task 13: NR adapter console host + public Docker image (battlescribe-spec submodule)

The NR adapter is a library today — give it an NDJSON console host and a public image. Work happens in the NESTED submodule `lib/wham/lib/battlescribe-spec` on branch `feat/muster-support` (continue the existing branch; commit + PR at the end; never touch `D:\repos\battlescribe-spec`).

**Files (inside `lib/wham/lib/battlescribe-spec/`):**
- Create: `src/BattleScribeSpec.NewRecruit.Adapter/BattleScribeSpec.NewRecruit.Adapter.csproj`
- Create: `src/BattleScribeSpec.NewRecruit.Adapter/Program.cs`
- Create: `docker/newrecruit-adapter/Dockerfile`
- Create: `.github/workflows/publish-nr-adapter.yml`
- Modify: `BattleScribeSpec.slnx` (add project)

**Interfaces:**
- Consumes: `AdapterHandler.RunAsync(Func<IRosterEngine>, TextReader, TextWriter, CancellationToken)` (`TestKit/Protocol/AdapterHandler.cs:16`), `NewRecruitRosterEngine` (`src/BattleScribeSpec.NewRecruit/NewRecruitRosterEngine.cs` — READ its constructor/lifecycle first: it likely needs `NewRecruitBrowser`/`NewRecruitEnginePool` setup; mirror however `BattleScribeSpec.Runner` or the Debugger constructs it today — find the construction site with `grep -rn "new NewRecruitRosterEngine" src tools tests`).
- Produces: console adapter runnable as `dotnet BattleScribeSpec.NewRecruit.Adapter.dll` (stdio NDJSON), and image `ghcr.io/warhub/bsspec-adapter-newrecruit:latest`.

- [ ] **Step 1: Console host**

`Program.cs` mirrors `src/BattleScribeSpec.ReferenceAdapter/Program.cs` (line 17 shape):

```csharp
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;

// NDJSON adapter host for the New Recruit engine (Playwright-driven).
// Speaks the same stdio protocol as ReferenceAdapter; ships as a public
// Docker image with the browser baked in (no proprietary JARs involved).
await AdapterHandler.RunAsync(
    engineFactory: () => /* construct NewRecruitRosterEngine the way the Runner does — copy the construction found via grep */,
    input: Console.In,
    output: Console.Out);
```

Copy any required Playwright bootstrap (browser install path env vars) from the existing NR test/runner setup — check `docker/docker-compose.yaml` and CI workflows in the spec repo for how NR runs headless today.

- [ ] **Step 2: Dockerfile**

```dockerfile
# docker/newrecruit-adapter/Dockerfile
# Public image: adapter binaries + Playwright browser. Publishes NO repo sources
# and NO BattleScribe JARs (this adapter has no JAR dependency).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/BattleScribeSpec.NewRecruit.Adapter -c Release -o /app

FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-noble
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/BattleScribeSpec.NewRecruit.Adapter.dll"]
```

(Pin the Playwright image tag to whatever `Microsoft.Playwright` version the NR project references — check `Directory.Packages.props` in the spec repo and match major.minor.)

- [ ] **Step 3: Publish workflow**

`.github/workflows/publish-nr-adapter.yml`: mirrors muster's own docker job — build on PRs, push `latest` + `sha-…` tags to `ghcr.io/warhub/bsspec-adapter-newrecruit` on push to the default branch, `docker/login-action@v3` with `GITHUB_TOKEN`, `permissions: packages: write`. The user makes the GHCR package public after first publish (same as muster's image).

- [ ] **Step 4: Build gate + commit + submodule bumps**

```bash
cd lib/wham/lib/battlescribe-spec
dotnet build -c Release src/BattleScribeSpec.NewRecruit.Adapter
git add -A && git commit -m "feat: NR adapter console host + public Docker image publishing"
cd ../..  # wham
git add lib/battlescribe-spec && git commit -m "chore: bump battlescribe-spec (NR adapter host)"
cd ../..  # muster
git add lib/wham && git commit -m "chore: bump wham (battlescribe-spec NR adapter host)"
```

(Full docker build can't run locally — no Docker on this machine; the PR's CI validates the image. The `dotnet build` gate catches everything else.)

---

### Task 14: E2E pilot demo — three beats (controller-led, NOT a subagent task)

Requires live GitHub + the merged muster image. Executed by the session controller after Tasks 1–13 merge (muster PR → main → image republished; battlescribe-spec + wham PRs opened).

- [ ] **Beat 0 — install the kit on the fork:** on `amis92/wh40k-11e` branch `muster-fixtures`: copy `kit/issue-form/report-a-data-bug.yml` → `.github/ISSUE_TEMPLATE/`, `kit/callers/muster-report.yml` → `.github/workflows/` with `data-source: "github:BSData/wh40k-11e"`. Push, merge to the fork's default branch (workflows for `issues` events only run from the default branch).
- [ ] **Beat 1 — confirmed:** file an issue on the fork using the form with a live NR share link for a Necrons/Orks list (grab a fresh link from a recent BSData/wh40k-11e issue, verify it still resolves via the RPC first) OR an inline YAML spec reproducing the wham#310-adjacent Deathmarks pin (65 observed). Expect: reply comment with per-engine matrix + `confirmed` label + snapshot block. Screenshot.
- [ ] **Beat 2 — not-reproducible after fix:** push a data change to the fork's default branch that alters the pinned value (or use the muster-demo branch's existing 60→65 edit inverted), comment `/muster check`. Expect: reply flips to `not-reproducible` with the value diff. Screenshot.
- [ ] **Beat 3 — promote:** comment `/muster promote`. Expect: PR opens adding `tests/rosters/report-issue-<n>.yaml` with re-pinned values; the muster PR-check workflow on that PR runs the new fixture green. Screenshot; merge.
- [ ] Measure and record: report-workflow wall time with `engines: wham` vs (if the NR adapter image is published by then) `wham newrecruit=docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest` — docker-in-container caveat: the reusable workflow runs muster IN a container; `docker:` engines need the host socket. If unavailable, run the NR engine measurement in a follow-up plain-runner job instead; record findings in the ledger + an issue for M3.5 if socket access blocks NR-in-CI.
- [ ] Update `docs/superpowers/specs/2026-07-13-muster-m3-executable-bug-reports-design.md` status → SHIPPED, memory file, progress ledger.

---

## Execution notes

- Tasks 1→11 are strictly ordered by interface dependency except: 5 ∥ (2,3,4) and 7 ∥ 5 — but run them sequentially anyway (single implementer at a time; subagent-driven-development forbids parallel implementers).
- Task 12 needs 10+11; Task 13 is independent of 5–12 (can slot anywhere after 1); Task 14 needs everything merged.
- Branch: `feat/m3-executable-bug-reports` (already exists, carries the spec). PR to `WarHub/muster` main at the end; org ruleset = PR-only, expect fresh-branch iterations if CI needs fixes.
- The Muster.Cli.Tests JSON report-shape tests from M2 will need updating exactly once (Task 3) — `--report` output becomes the MultiRunReport envelope. That is the only intentional breaking change to existing outputs; `summary`/`markdown` human outputs stay recognizable, and the Action's report-path contract is unchanged.
