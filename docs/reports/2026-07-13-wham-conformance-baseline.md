# wham conformance baseline (M0)

**Date:** 2026-07-13
**Purpose:** M0 go/no-go gate — establish a measured baseline of how well wham (the
`WarHub.ArmouryModel.RosterEngine`) conforms to the `battlescribe-spec` conformance
suite, before muster is built to run this measurement automatically in CI.

**Audience note:** this assumes familiarity with BattleScribe (game systems,
catalogues, roster building, selection/cost/constraint/modifier semantics) but not
with wham's internals. wham is a .NET reimplementation of a BattleScribe-compatible
roster editor; `battlescribe-spec` is a shared, engine-agnostic YAML spec suite that
multiple roster engines (wham, NewRecruit/"newrecruit", others) are checked against.

## Repro info

- wham repo: `D:\repos\wham`
- wham commit: `b691e5bdef96a5282b2ef906b680af88357924c0` (branch `feature/roster-engine`, clean tree)
  - Note: the brief's Step 1 assumed a `main` branch checkout. Locally, `main` points
    at an older commit (`4b7889a`) and `feature/roster-engine` is the branch actually
    checked out at `b691e5b`, clean. Per instructions this local state is authoritative
    for measurement purposes (no `git pull`, no branch switch, no code changes) — the
    important fact is the exact commit measured, which is `b691e5b`.
- `battlescribe-spec` submodule commit: `e40d06c7995c9e6e30123055c56d81cbcb2e4a49` (`lib/battlescribe-spec`, `heads/main`)

## Step 1: run wham's conformance suite

Commands actually run (adapted from the brief — see deviations below):

```powershell
cd D:\repos\wham
dotnet tool restore
dotnet build tests\WarHub.ArmouryModel.RosterEngine.Tests\WarHub.ArmouryModel.RosterEngine.Tests.csproj
dotnet test tests\WarHub.ArmouryModel.RosterEngine.Tests\WarHub.ArmouryModel.RosterEngine.Tests.csproj `
  --no-build --filter "FullyQualifiedName~WarHub.ArmouryModel.RosterEngine.Tests.ConformanceTests" `
  --logger "trx;LogFileName=wham-conformance.trx"
```

**Deviations from the brief, and why:**

1. Skipped `git switch main && git pull && git submodule update --init --recursive` —
   remote reachability wasn't verified and the task instructions say local state is
   authoritative; the checked-out tree at `b691e5b` was already clean with the
   submodule pinned to `e40d06c`, matching what `git submodule status` reported.
2. `dotnet build` on the full solution (`dotnet build` with no project argument)
   failed with 17 errors, all in `WarHub.ArmouryModel.Source.CodeGeneration.Tests`
   (`CS0246`/`CS0009` — a stale/corrupt incremental-build artifact under
   `artifacts/obj/...GeneratedCode`, unrelated to the roster engine or the source
   generator itself). Since this task is measurement-only (no wham code changes
   permitted) and only `WarHub.ArmouryModel.RosterEngine.Tests` is needed, I built
   that project directly instead of the whole solution. It built clean: 0 errors,
   0 warnings from that project (65 warnings/0 errors were pre-existing nullable/
   analyzer noise across the solution, not from this project).

**Result:**

```
Test run for D:\repos\wham\artifacts\bin\WarHub.ArmouryModel.RosterEngine.Tests\debug\WarHub.ArmouryModel.RosterEngine.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Results File: D:\repos\wham\tests\WarHub.ArmouryModel.RosterEngine.Tests\TestResults\wham-conformance.trx

Passed!  - Failed:     0, Passed:   351, Skipped:     0, Total:   351, Duration: 657 ms - WarHub.ArmouryModel.RosterEngine.Tests.dll (net10.0)
```

Confirmed via the TRX `<Counters>` node: `total="351" executed="351" passed="351" failed="0"`.

**Important interpretation caveat (per the brief):** in `ConformanceTests.cs`, an xUnit
"pass" means *expectation met*, not "engine behaved correctly" — specs marked
`wham: fail` are expected to fail, and the xUnit test asserts `result.Passed == false`
for those. So a 100% xUnit pass rate would still be compatible with wham failing
specs, as long as those specs were pre-annotated as expected failures. Step 2 checks
how many such annotations exist.

## Step 2: per-spec wham expectations in the spec suite

```powershell
cd D:\repos\wham\lib\battlescribe-spec
$specs = Get-ChildItem specs -Recurse -Filter *.yaml
$total = $specs.Count
$whamLines = $specs | Select-String -Pattern '^\s*wham\s*:\s*(\w+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
$whamLines | Group-Object | Sort-Object Count -Descending | Format-Table Name, Count
Write-Host "Total specs: $total; specs with explicit wham expectation: $($whamLines.Count)"
```

**Output:**

```
Total specs: 401; specs with explicit wham expectation: 0
```

i.e. **zero** spec files anywhere in the suite currently carry an `engines: wham: ...`
entry (verified independently with a plain case-insensitive `grep -ri wham` over
`specs/`, which also returned 0 hits). Every spec in the suite defaults to
"expected pass" for wham. By contrast, 38 roster spec files carry explicit
`newrecruit:` overrides (fail/skip), so the annotation mechanism is exercised and
working — wham genuinely has none right now, not because the field is unused.

This wasn't always true. wham's own history shows the opposite state at earlier
points:

```
b8a318a chore: add wham engine overrides for zero-fill cost expectations (steps 5 and 18)
70f44c9 Add wham engine override for scope-child-id-filter ordering
d7beca0 Add wham: fail to undefined-behavior modifier-group specs
```

and the commit that bumped the spec submodule to the currently-pinned `e40d06c`
is titled *"chore: update battlescribe-spec to e40d06c, remove zero-fill, fix cost
scaling and evaluator correctness (#306)"* — i.e. the known gaps that used to be
marked `wham: fail` were fixed in wham itself and the xfail annotations removed,
rather than the suite being weakened. An earlier commit (`231725c`) is titled
*"Adapt roster engine to ID-based IRosterEngine spec (329/329 specs)"*, showing this
is a maintained, previously-100%-passing target, not a one-off.

### Spec population breakdown

`specs/` contains two disjoint domains, only one of which applies to a roster engine
like wham:

| Domain | Files | Applicable to wham? |
|---|---:|---|
| `specs/gamedata/**` | 48 | No — these test catalogue/game-system *editing* tools (e.g. NR's editor), using a different YAML schema (`GameDataSpecFile`) with editor-specific actions like `addEntry`. They fail to deserialize as roster `SpecFile` and are silently skipped by `ConformanceTests.AllSpecs()`'s `try { LoadEmbedded(...) } catch { continue; }` — this was confirmed by code inspection, not just by count arithmetic. |
| `specs/roster/**` | 353 | Yes — these are the roster-building/evaluation specs wham's `ConformanceTests` targets. |

Within `specs/roster/**` (353 files), by category:

| Category | Count |
|---|---:|
| selection | 95 |
| modifier | 60 |
| constraint | 39 |
| condition | 37 |
| cost | 23 |
| force | 21 |
| scope | 14 |
| entry-id | 15 |
| roster | 9 |
| deep-nesting | 6 |
| ordering | 6 |
| auto-select | 5 |
| catalogue | 5 |
| entry-group | 4 |
| gamesystem | 4 |
| category | 3 |
| entry-link | 3 |
| protocol | 2 |
| real-world | 2 |
| **Total** | **353** |

Of the 353 roster specs, 2 (`specs/roster/real-world/wh40k-10e-create-army.yaml` and
`wh40k-10e-space-marines-army.yaml`) declare `setup.dataSource:
"github:BSData/wh40k-10e@v10.6.0"` — they require fetching a live, real-world
BattleScribe catalogue over the network rather than using inline test fixtures.
`ConformanceTests.AllSpecs()` explicitly excludes any spec with
`spec.Setup.DataSource is { Length: > 0 }`, so these 2 are not exercised by this test
run at all (not pass, not fail, not skip — simply not included in the 351-item theory).
That leaves **351 specs actually exercised** (353 − 2), matching the Step 1 test count
exactly.

## Derived conformance

Formula per the brief: `(total − roster-DataSource-specs − skip − fail) / applicable`.

- total (roster-domain) specs: 353
- roster-DataSource-specs (excluded, not run): 2
- specs with explicit `wham: skip`: 0
- specs with explicit `wham: fail`: 0
- applicable/exercised: 353 − 2 = 351
- **conformance = (353 − 2 − 0 − 0) / 351 = 351 / 351 = 100%**

This matches the raw xUnit pass count directly, *because* there are zero xfail
annotations to obscure the picture this time: every one of the 351 exercised specs
both ran and produced the state BattleScribe-spec says it should.

## Failing / skipped specs, by category

None. There are no `wham: fail` or `wham: skip` entries anywhere in the current
`battlescribe-spec` suite (commit `e40d06c`), and the live xUnit run corroborates
this with 0 failures and 0 skips out of 351 executed tests.

The only specs not covered by this measurement are the 2 real-world/DataSource
specs (`real-world` category, network-dependent, BSData wh40k-10e catalogue) — these
are excluded by design from `ConformanceTests`, not failing or skipped in the
xUnit-result sense. They represent unmeasured territory, not a gap in the 100%
figure above.

## Go/no-go verdict

**GO.** The failing-categories check from the brief is trivially satisfied — there are
no failing categories at all, let alone `cost`, `constraint`, or `selection` (the
three that dominate golden-roster assertions: 23 + 39 + 95 = 157 of 353 roster specs,
45% of the suite). wham currently passes 100% (351/351) of the applicable,
exercised `battlescribe-spec` conformance suite at commit `e40d06c`, and this isn't
an artifact of an untested engine: wham's own git history shows prior rounds of
`wham: fail`/`wham: skip` annotations for known gaps (zero-fill cost handling,
scope/child-id-filter ordering, undefined-behavior modifier groups) that were
subsequently *fixed in the engine* and the annotations removed, and an earlier
"329/329 specs" milestone before the suite grew to its current size. wham is a
viable roster-engine target for muster's pilot CI harness.

**Caveats to carry into M4 (capability manifest) and beyond:**

1. The 2 real-world/DataSource specs are not exercised by this baseline (network
   dependency) — they should be run separately (or with the `DataSourceResolver`
   wired to a cache) before claiming coverage of real published game-system data,
   not just synthetic fixtures.
2. `specs/gamedata/**` (48 specs) covers a different capability — catalogue/game-system
   *editing* — that wham's `RosterEngine.Tests` doesn't attempt at all. If muster's
   scope ever expands to editor-style operations (as opposed to roster building),
   this 100% figure says nothing about that surface.
3. This is a point-in-time measurement (`e40d06c` / `b691e5b`); zero xfail entries
   for wham means the *current* suite makes no known accommodations for wham, so a
   regression anywhere would show up as an actual `Failed!` in CI, not a silent xfail
   flip. That's the desired property for a CI gate, but it also means the 100%
   figure has no slack — any future spec addition that wham doesn't yet handle will
   immediately drop this number, which is expected and fine, not a sign this
   baseline was wrong.

## Raw artifacts

- TRX: `D:\repos\wham\tests\WarHub.ArmouryModel.RosterEngine.Tests\TestResults\wham-conformance.trx`
  (`<Counters total="351" executed="351" passed="351" failed="0" .../>`)
