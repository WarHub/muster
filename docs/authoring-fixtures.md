# Authoring golden-roster fixtures

A **fixture** is a battlescribe-spec roster DSL YAML file that drives wham's
roster engine against real (or inline) game data and asserts on the
resulting roster state. `muster test` discovers every `*.yaml` file under a
fixtures directory (recursively) and runs each one.

This guide covers the fixture schema **as it is actually implemented**
today (not the design-doc version), the id-discovery recipes used to author
the pilot Necrons fixtures in `wh40k-11e/tests/rosters/`, exit-code
semantics, and one full worked example.

## Fixture schema

```yaml
id: <string>                  # unique fixture id, also used as the [PASS]/[FAIL] label
category: <string>            # freeform grouping, e.g. "real-world", "constraint"
description: <string>         # human-readable summary (block scalar `>` is fine)
tags: [tag1, tag2]             # freeform, optional

setup:
  dataSource: "github:{org}/{repo}[@{ref}]"   # OR "local:{path}" — a SINGLE STRING, not a list

steps:
  - id: <string>               # optional; lets later steps reference this step's outputs
    action: <actionName>       # one of: addForce, addChildForce, removeForce, selectEntry,
                                # selectChildEntry, deselectSelection, setSelectionCount,
                                # duplicateSelection, duplicateForce, setCostLimit, setCustomization
    forceEntryId: <id>         # addForce / addChildForce
    catalogueId: <id>          # addForce / addChildForce — REQUIRED in dataSource mode (see below)
    forceId: ${{ steps.<id>.forceId }}   # most actions reference a prior step's force
    entryId: <id>               # selectEntry / selectChildEntry
    selectionId: ${{ steps.<id>.selections.<entryId> }}  # deselectSelection / setSelectionCount / etc.

  - expectedState:
      costs: [...]              # ROSTER-level cost totals — see "known gap" below
      costCount: <int>
      forces: [...]
      errors: [...]             # exact-set error assertion
      errorsContain: [...]      # subset error assertion
      errorCount: <int>
      # ...and more; see BattleScribeSpec.Roster.ExpectedStateDef / RosterRunner.cs
      # for the full field list (forceCount, selectionCount, name, gameSystemId, ...).
```

A step has either `action` (do something) or `expectedState` (assert
something) — never both.

### `setup.dataSource` is a single string

Unlike some earlier design drafts, `setup.dataSource` is **one string**, not
a list. Two schemes are supported:

- `github:{org}/{repo}[@{ref}]` — resolved against `muster test --data
  <root>`'s cache layout: `<root>/github/{org}/{repo}/{ref-or-latest}/`.
  `muster test` refuses to fall through to a live `git clone` — the fixture
  is reported **inconclusive** if that directory isn't already populated
  (hermeticity gate in `TestCommand.RunFixture`).
- `local:{path}` — resolved as a literal directory path.

To run a fixture against a real data repo without a live clone, stage the
repo's files under the expected cache path yourself:

```powershell
$dataRoot = "C:\some\temp\dir"
$dest = "$dataRoot\github\BSData\wh40k-11e\main"
New-Item -ItemType Directory -Force $dest
Copy-Item "D:\repos\wh40k-11e\*.yaml" $dest   # top-level files only — see note below
dotnet run --project src\Muster.Cli -- test --data $dataRoot --fixtures <fixturesDir> --output summary
```

**File enumeration is recursive.** `RosterRunner.SetupFromDataSource` reads
every `.gst`, `.cat`, `.yaml`, and `.yml` file under the resolved directory
via `SearchOption.AllDirectories` — it does *not* matter whether you nest
files in subfolders. The pilot fixtures still copy only the data repo's
top-level `*.yaml` files (skipping `tests/` and `ANALYSIS.md`) to keep the
staged data root minimal and to avoid accidentally feeding the harness's
own fixture files back into the game-data compilation.

### `catalogueId` is required on `addForce` in dataSource mode

When `setup.dataSource` is used, `addForce` (and `addChildForce`) must
specify `catalogueId` explicitly — there's no single implicit catalogue to
fall back to once real data (with dozens of catalogue files) is loaded.
`catalogueId` should be the catalogue whose selection entries you intend to
pick from later in the fixture (e.g. the faction catalogue, not the
game-system file).

### Referencing prior-step outputs

Steps that produce a force or selection can be tagged with `id:`, and later
steps reference their outputs with `${{ steps.<id>.<field> }}`:

- `${{ steps.<id>.forceId }}` — the force id created by `addForce`.
- `${{ steps.<id>.selections.<entryId> }}` — the selection id created by
  selecting `entryId` in that step (used by `deselectSelection`,
  `setSelectionCount`, `selectChildEntry`, etc.).

## Id-discovery recipes

Real BattleScribe/wham data has no human-friendly identifiers in the DSL —
every reference is a GUID-like id. These are the recipes used to find the
ids in the pilot fixtures, run from the data repo root
(`D:\repos\wh40k-11e`):

```powershell
# Game system id (needed nowhere directly in fixtures, but useful to sanity-check
# you're pointed at the right file):
Select-String -Path "Warhammer 40,000.yaml" -Pattern "^\s*id: sys-"

# A forceEntry id + name (e.g. "Army Roster") — searched inside gameSystem.forceEntries:
Select-String -Path "Warhammer 40,000.yaml" -Pattern "forceEntries:" -Context 0,12

# The pts costType id — confirm it appears in the gameSystem's costTypes,
# and note its `hidden` toggle conditions (see "known gap" below):
Select-String -Path "Warhammer 40,000.yaml" -Pattern "name: pts" -Context 3,0
# -> id: 51b2-306e-1021-d207

# A catalogue's own id + name (top-level `catalogue:` block, near the end of the file):
Select-String -Path "Necrons.yaml" -Pattern "^  id: |^  name: |^  gameSystemId: " | Select-Object -Last 6

# A unit entry id + its flat pts cost — grep every top-level `type: unit` entry,
# then read enough context above/below to find its sibling `id:`/`name:`/`costs:`:
Select-String -Path "Necrons.yaml" -Pattern "  type: unit" -Context 14,0 | Select-Object -First 3

# A max-constraint you can violate (any scope) — the value/scope typically
# follow `type: max` by a few lines:
Select-String -Path "Necrons.yaml" -Pattern "type: max" -Context 2,6 |
  Select-String -Pattern "scope: roster"

# Once you have a candidate entry id, confirm it's reachable via a root
# entryLink (i.e. actually selectable via the `selectEntry` action) —
# entryLinks live in a `  entryLinks:` top-level block and reference the
# entry by `targetId`:
Select-String -Path "Necrons.yaml" -Pattern "targetId: <candidate-id>"
```

Where a fixture asserts error-shape (`errors:`/`errorsContain:`), check the
DSL's assertion shape against an existing spec before authoring:

```powershell
Get-ChildItem D:\repos\muster\lib\wham\lib\battlescribe-spec\specs\roster -Recurse |
  Select-String -List "validationErrors" -ErrorAction SilentlyContinue
# or, more directly:
Select-String -Path D:\repos\muster\lib\wham\lib\battlescribe-spec\specs\roster\constraint\constraint-max-violation.yaml -Pattern "errors"
```

An error assertion entry has the shape:

```yaml
- on: "<ownerType> <ownerEntryId>"   # e.g. "selection <entryId>" or "category <catEntryId>"
  from: "<entryId>/<constraintId>"    # the entry and constraint that produced the violation
  messageContains: "<substring>"       # optional
```

`errors:` asserts the **exact set** of validation errors (fails if there
are extras); `errorsContain:` asserts a **subset**, ignoring any other
errors present. Prefer `errorsContain:` against real data-source fixtures —
real catalogues routinely carry unrelated, pre-existing constraint
violations (e.g. unresolved mandatory sub-choices) that have nothing to do
with what you're testing.

## Known gap: roster-level `costs` vs entryLink-sourced entries

While authoring the pilot fixtures we found that **`expectedState.costs`
(the roster-level cost rollup) is silently empty for any cost type that is
only reachable through a catalogue's `entryLinks`** — which, in
`wh40k-11e`, is *every* combat unit (the whole repo uses the modern
BattleScribe schema: real entries live in `sharedSelectionEntries` /
`sharedSelectionEntryGroups`, exposed to the roster tree only via
`entryLinks`).

Cause: `StateMapper.MapRosterState` builds a set of "referenced cost type
ids" by walking `EntryResolver.GetAvailableEntries(catalogue)` and calling
`CollectReferencedCostTypes(entry.Symbol, ...)` on each
(`lib/wham/src/WarHub.ArmouryModel.RosterEngine.Spec/StateMapper.cs`). For
entries reached through an entryLink, `EntryResolver.AddEntryOrFlatten`
records `Symbol = entry` — the **link container itself** — not the
resolved target
(`lib/wham/src/WarHub.ArmouryModel.RosterEngine/EntryResolver.cs`). The
link symbol's own `.Costs` appears empty, so the pts cost type never makes
it into `referencedCostTypeIds`, and `ComputeRosterCosts` (which only
emits cost types present in that set) silently drops it from the
roster-level total — even though the *individual selection's* costs
(`forces[].selections[].costs`), which are read from the resolved/effective
symbol via a different code path (`MapSelectionCosts`), are computed
correctly.

Practical effect: **assert costs at the selection level
(`forces[].selections[].costs`), not the roster level
(`expectedState.costs`), for any fixture against real BattleScribe-schema
data.** `necrons-single-unit-cost.yaml` does this. `necrons-force-empty.yaml`
still asserts `costs: []` at the roster level, but note that assertion is
now satisfied unconditionally by this gap (there are no costed selections
in that fixture either way, so it happens to also be true) — it isn't
independent proof of correct zero-cost behavior once real entryLink-sourced
data is involved.

This is a real wham defect, not a fixture-authoring mistake; it should be
fixed by having `AddEntryOrFlatten` store the *resolved* symbol (or by
having `CollectReferencedCostTypes` resolve links before reading
`.Costs`/`.ChildSelectionEntries`).

## Exit-code semantics

`muster test` exits with:

| Code | Meaning |
|------|---------|
| `0`  | All fixtures ran and every assertion passed. |
| `1`  | At least one fixture ran and had a genuine assertion failure (`Failed > 0`). Takes priority over inconclusive. |
| `2`  | No fixture failed on assertions, but at least one was **inconclusive** — a harness/engine crash, a fixture parse error, or (most commonly for dataSource fixtures) an unpopulated, non-hermetic data source. Also returned for command-level setup problems (missing `--data`/`--fixtures` directory, no fixtures found, top-level harness exception). |

Inconclusive fixtures are reported separately from failures precisely so
that a real assertion mismatch (a signal that the engine or the fixture's
expectations are wrong) is never masked by, or confused with, environment
problems (missing data, crashes). CI should treat exit code `2` as "needs
attention" but distinct from a red build on `1`.

### How the GitHub Action surfaces exit code 2

The published Action (`entrypoint.sh`) maps exit code `2` to a **green**
check: it prints a `::warning::` annotation and then exits `0`, rather than
propagating the inconclusive status as a failing check. This is deliberate —
it's the "abstain, don't lie" design: muster would rather stay silent (green)
about something it couldn't actually verify than paint an environment or
harness problem the same red as a genuine data regression.

The consequence is worth knowing before you rely on it: a PR that, say,
deletes a data-catalogue entry that a fixture references will make that
fixture's run inconclusive, not failed — the check goes green with only a
`::warning::` line in the job log (and in `GITHUB_STEP_SUMMARY`). Nothing
blocks the merge. Maintainers should get in the habit of scanning Action logs
for `::warning::muster run was inconclusive` rather than assuming a green
check means every fixture actually ran and passed. A `fail-on-inconclusive`
Action input (to let a repo opt into treating exit code `2` as red) is a
planned follow-up, not yet implemented.

## Worked example

`necrons-single-unit-cost.yaml` (from `wh40k-11e/tests/rosters/`), in full:

```yaml
id: necrons-single-unit-cost
category: real-world
description: >
  Add a Necrons force and select a single Deathmarks squad — verify the
  selection's pts cost matches the flat cost recorded on the entry in
  Necrons.yaml (costs: pts value: 60).
  ...
tags: [wh40k-11e, real-world, necrons, cost, known-gap]

setup:
  dataSource: "github:BSData/wh40k-11e@main"

steps:
  - id: add-force
    action: addForce
    forceEntryId: bb9d-299a-ed60-2d8a # "Army Roster" forceEntry, Warhammer 40,000.yaml
    catalogueId: b654-a18a-ea1-3bf2   # "Xenos - Necrons" catalogue, Necrons.yaml

  - action: selectEntry
    forceId: ${{ steps.add-force.forceId }}
    entryId: b7d7-14c9-63c1-ded5 # "Deathmarks" unit, Necrons.yaml (costs: pts value: 60)

  - expectedState:
      forces:
        - selectionCount: 2
          selections:
            - name: "Show/Hide Options"
              type: upgrade
            - name: "Deathmarks"
              type: unit
              costs:
                - name: "pts"
                  value: 60
                - name: "Crusade Points"
                  value: 0
                - name: "Crusade: Battle Honours"
                  value: 0
                - name: "Crusade: Experience"
                  value: 0
                - name: "Crusade: Weapon Modifications"
                  value: 0
```

Running it (once the data root is staged as shown above):

```
dotnet run --project src\Muster.Cli -- test --data <dataRoot> --fixtures D:\repos\wh40k-11e\tests\rosters --output summary
[PASS] necrons-force-empty
[PASS] necrons-max-constraint
[PASS] necrons-single-unit-cost
Results: 3 passed, 0 failed, 0 inconclusive (57.1s)
```

Notes on what this example demonstrates:

- `addForce` auto-selects one free "Show/Hide Options" display toggle —
  this is standard mandatory-entry auto-selection behavior, not something
  the fixture author has to model explicitly.
- `selectEntry` finds `b7d7-14c9-63c1-ded5` even though it's defined inside
  `Necrons.yaml`'s `sharedSelectionEntries:` pool, because it's exposed at
  the catalogue root via an `entryLinks:` entry with a matching
  `targetId` — `EntryResolver.FindByEntryId` falls back to matching the
  *resolved* target's id when no composite link-prefixed id matches.
- The `costs:` list under a selection must be **exhaustive** — the
  BattleScribe data authors every cost type (`pts` plus four zero-valued
  Crusade tracking types) on every entry, and `RosterRunner.AssertSelections`
  fails if the actual cost list has more entries than the expected list.

See `necrons-max-constraint.yaml` for the error-assertion (`errorsContain`)
pattern, and `necrons-force-empty.yaml` for the simplest possible
`addForce`-only fixture.
