# Muster — CI Harness for Wargame Data Repos (v1 Design)

**Date:** 2026-07-13
**Status:** Approved design, pre-implementation
**Repo:** `WarHub/muster` (this repo)

## Context and strategy

The long-term goal is a full open-source toolchain for the BSData community (BattleScribe-format wargame data): authoring editors, a debuggable roster engine, GitHub-integrated web editing, roster sharing, and a query API. The strategic posture is **infrastructure-first**: build open-source tooling the community adopts and depends on; commercial opportunities come later, once the toolchain is indispensable.

The chosen wedge — the first thing the community adopts — is a **CI test harness for data repositories**. It leverages the ecosystem's keystone asset, the `battlescribe-spec` conformance suite (~478 YAML specs, adapter protocol, cross-engine compare), requires almost no UI, and installs at repo level, where dependence compounds.

Muster is **the data author's toolchain** — the `cargo` of this ecosystem — not just a CI action. v1 ships the CI harness; the repo is scoped and named for the full toolchain.

### Decisions taken (with alternatives rejected)

| Decision | Chosen | Rejected alternatives |
|---|---|---|
| First wedge | CI test harness for data repos | Web roster builder; web data editor; data-format modernization |
| v1 scope | Golden-roster PR checks **and** executable bug reports, both from day one | Either alone; full spec-suite runs against data repos |
| Verdict engine | **wham** (`WhamRosterEngine`) from day one, with abstention guardrails | Legacy engine (IKVM) with wham in shadow; legacy only; New Recruit via Playwright |
| Pilot | `amis92/wh40k-11e` fork (real 40k data, zero org politics) | Synthetic demo repo; straight to BSData org; smaller-system repo |
| Name | `muster` ("pass muster") | `quartermaster`, `armoury`, `bsdata-toolkit`, `bsdata-ci` |

## 1. Product contract

A GitHub Action + reusable workflow that a data repo installs once. Two author-facing capabilities:

### Golden-roster PR checks

The data repo grows a `tests/rosters/` directory of fixture rosters with expected outcomes (points totals, constraint verdicts, selection validity). Every PR re-evaluates all fixtures against the changed data and posts a check plus a PR comment: what changed, what broke — blast radius as a diff table. Green check = "no golden roster changed behavior."

### Executable bug reports

An issue form ("Report a data bug") where the reporter pastes a roster and states expected vs. actual behavior. A workflow evaluates the roster against latest data, posts the engine's actual values, and labels the issue `confirmed` / `not-reproducible` / `needs-info`. When a fix PR lands, the roster can be **promoted to a permanent golden fixture** — bugs become regression tests.

## 2. Architecture

A thin CLI (`muster`) with a four-stage pipeline: **load data → load fixtures → evaluate → render report**.

- **Data loading.** `BSData/wh40k-11e` stores YAML-serialized BattleScribe schema; wham reads XML. A YAML⇄wham-model reader is new **library work that lands in wham**, not in muster (the schema is identical; only serialization differs). Classic XML `.cat`/`.gst` repos work out of the box.
- **Fixture format.** Reuse the roster YAML DSL from `battlescribe-spec` (365 existing roster specs prove the format) rather than inventing one. Escape hatch: raw `.ros`/`.rosz` accepted in fixtures and issue forms.
- **Evaluation.** `WhamRosterEngine` through the existing `SpecRosterEngineAdapter` — the adapter protocol is already the seam between spec-shaped input and an engine.
- **Reporting.** Markdown renderers for the PR comment, check summary, and issue reply; machine-readable JSON alongside.

## 3. The wham-trust guardrail

wham gives verdicts from day one, so a conformance gap must surface as **abstention, never a wrong answer**:

- **Capability manifest.** Each wham release ships its spec-suite results (which specs pass). Fixtures exercising features wham fails are reported **neutral** — "not yet supported by engine" — visible, honest, never wrong.
- **Nightly shadow-compare.** The legacy IKVM-hosted BattleScribe engine re-evaluates all fixtures nightly; `bs-spec compare` flags verdict divergence. Disagreements auto-file issues on wham. The harness itself becomes the machine that finishes wham, with real 40k data as a permanent conformance workload.
- **M0 is measurement.** Before anything ships, run wham against the full spec suite and a batch of real 40k rosters to establish its pass rate. That number decides whether v1 abstains on 5% or 40% of fixtures — and whether the pilot is viable yet.

## 4. Packaging

**Repo `WarHub/muster` — the data author's toolchain.** Contents:

- the `muster` CLI — subcommands `test` (golden rosters), `lint`, `fmt`, `convert` (YAML⇄XML), `diff` (blast radius); later `serve` (query API);
- the GitHub Action wrapping it (`action.yml` at repo root, Docker-based so the .NET runtime is invisible to adopters, versioned together with the CLI);
- reusable workflows and the issue-form kit;
- author-facing docs site (GitHub Pages).

NuGet package id `WarHub.Muster` with `ToolCommandName=muster` (bare-name squatting on NuGet doesn't affect the command; verify availability at scaffold time).

**Boundary rule:** muster owns everything an author touches from a terminal or a workflow file. Libraries stay upstream — `wham` gets the YAML reader and engine work; `battlescribe-spec` keeps the fixture DSL and oracle machinery, consumed as TestKit packages. GUIs (web editor, roster builder, hot-reload authoring) get their own repos later and consume the same packages.

Tagline: *"Your data passes muster."*

## 5. Error handling

- Engine crash, timeout, or unparseable data → **neutral check with diagnostics attached, never a red X**. A harness that fails loudly on its own bugs gets uninstalled within a week.
- Per-catalogue partitioning: one broken file doesn't sink the whole run.
- Hard time budget per fixture, with the offending fixture named in the report.
- Issue-form input is hostile-by-default: parse failures produce a polite `needs-info` reply, not a stack trace.

## 6. Testing

Dogfooding all the way down:

- muster's own CI runs the `battlescribe-spec` suite against the wham engine it bundles;
- integration tests pin a snapshot of wh40k-11e data + fixtures;
- E2E exercises the real Action on the pilot fork;
- issue-form parsing (form → roster) gets adversarial/fuzz-style tests, since it accepts input from strangers.

## 7. Milestones

- **M0 — Measure wham.** Full spec-suite pass rate + a real-40k-roster batch. Go/no-go data; sizes the abstention surface.
- **M1 — Local CLI.** `muster test` evaluates a golden roster against wh40k-11e YAML data locally (requires the wham YAML reader).
- **M2 — Action + PR report.** Blast-radius report running on the pilot fork.
- **M3 — Executable bug reports.** Issue-form evaluation + fixture-promotion flow.
- **M4 — Trust machinery.** Capability manifest + nightly legacy shadow-compare.
- **M5 — Pitch to BSData org.** With a link to months of green checks, not a proposal document.

## 8. Explicitly deferred (later sub-projects, same substrate)

Web data editor with GitHub integration (branch/commit via gh login, no local files); reference roster builder with data-in-URL sharing; live hot-reload data-editor-plus-roster; cross-entry dependency and modifier-evaluation visualization; GraphQL/query API as a hosted service. Each becomes its own spec → plan cycle and reuses the engine, the YAML reader, and the fixture corpus this project builds.

`battlescribe-web` (CheerpJ-hosted legacy engine) stays alive as the reference oracle and a future UI shell — nothing here discards it.

## Open questions (tracked, not blocking)

1. wham's actual current pass rate against the spec suite — answered by M0.
2. Exact YAML dialect of `BSData/wh40k-11e` vs. wham's XML schema expectations (field ordering, id formats) — answered during the wham YAML-reader work.
3. NuGet `WarHub.Muster` / GitHub `WarHub/muster` availability — verify at scaffold time.
4. Whether issue-form rosters accept New Recruit export formats beyond `.ros`/`.rosz` — decide at M3.
