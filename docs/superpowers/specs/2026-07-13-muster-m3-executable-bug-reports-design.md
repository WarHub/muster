# Muster M3 — Executable Bug Reports: Design

Date: 2026-07-13. Status: approved. Prior art: [M0–M2 design](2026-07-13-muster-ci-harness-design.md) (shipped: WarHub/muster live, blast-radius Action working on the pilot fork).

## 0. Decisions log

| Decision | Choice | Rejected |
|---|---|---|
| Scope | Spec-M3 (issue-form evaluation + fixture promotion), muster#4 folded in | Hardening-only cycle; combined mega-cycle |
| Roster inputs (v1) | YAML DSL steps, `.ros`/`.rosz`, **and New Recruit share links (fetched)** | YAML-only; ignoring the NR stream |
| Confirm logic | Replay vs **observed** values (assertions pin what the reporter saw) | Structured expected-value form field; report-only with manual labels |
| Promotion | `/muster promote` slash command → PR (write-permission gated) | Label-driven; local-CLI-only |
| Architecture | **Everything becomes a spec** — all inputs convert to fixture-DSL YAML, executed by the existing RosterRunner pipeline | Bespoke replay comparator in wham; report-only |
| Engines | **Per-engine evaluation everywhere** (report, test, diff) via an engine registry; verdicts gain `engine-gap`; **governing engine configurable, default New Recruit** (the ecosystem's current main app) | wham-only; reports-only matrix; wham or legacy BS as default governor |

## 1. Product contract

A data repo that already uses the muster Action gains a second install: an issue form ("Report a data bug") plus a reusable workflow. The flow:

1. **Report.** A reporter files the issue form (or New Recruit auto-files its own report — recognized too). The body carries a roster as one of: NR share link, `.ros`/`.rosz` attachment URL, or inline fixture-DSL YAML steps.
2. **Evaluate.** The workflow runs the muster container. Muster parses the body, obtains the roster, converts it to a fixture-DSL spec whose assertions **pin the observed values** (what the reporter's app showed), and evaluates it against latest data with every available engine (§5).
3. **Label + reply.** Sticky comment with a **per-engine result matrix** (see §5) and the verdict; label applied:
   - `confirmed` — the governing engine reproduces what the reporter saw. The bug reproduces.
   - `not-reproducible` — the governing engine computes different values (often "already fixed"); reply shows the exact per-engine diffs.
   - `engine-gap` — engines that ran disagree with *each other*; added alongside the governing verdict and auto-flagged for the wham backlog.
   - `needs-info` — unusable input (bad link, rotted list, unparseable roster). Polite reply, never a stack trace.
   - `inconclusive` — every available engine abstained (M2 discipline unchanged): visible, honest, never wrong.
4. **Snapshot.** The reply embeds the generated spec in a collapsed `<details>` block. This is the durable copy — NR links rot (BSData/wh40k-11e#234's list is already gone), so after first evaluation the issue thread is self-contained.
5. **Promote.** After a fix lands, a maintainer comments `/muster promote`. The workflow re-reads the snapshot from its own comment, re-evaluates, re-pins assertions to the current (post-fix, correct) engine values, and opens a PR adding the fixture to `tests/rosters/` with the issue linked. Human correctness review happens exactly once — on that PR. Bugs become regression tests.

## 2. Why "everything becomes a spec"

All three inputs normalize to the battlescribe-spec roster DSL, and the existing pipeline (SpecLoader → RosterRunner → SpecRosterEngineAdapter → wham) executes them. No second execution path, no bespoke comparison engine:

- **Confirmation is assertion semantics inverted at the label layer.** Assertions pin observed (buggy) values; PASS means "reproduced" ⇒ `confirmed`. FAIL lists per-value diffs ⇒ `not-reproducible`.
- **The snapshot IS the promotable artifact.** One file format from report to regression test; promotion just re-pins expected values.
- **`muster convert` finally exists** — the CLI seam the M0–M2 spec named but didn't build.

## 3. Input formats (exploration findings, verified 2026-07-13)

### New Recruit share links

- NR auto-files bug reports on BSData repos itself; the body pattern is `**Problem:** … **Expected:** … **List:** https://www.newrecruit.eu/app/list/<key>` (see BSData/wh40k-11e#234). This is the dominant existing report stream — supporting it makes real reports executable with zero reporter effort.
- Fetch: `POST https://www.newrecruit.eu/api/rpc` body `{"method":"open_share_link","params":["<key>"]}` — answers anonymously (verified live; discovered via the NR app bundle, confirmed by instrumenting the page's fetch). **Undocumented internal API**: any failure/shape drift degrades to `needs-info`, never a crash. Reach out to NR maintainers about a stable contract when pitching BSData (M5).
- Payload: `{name, totalCost, totalCosts[{name,value,typeId}], bsid_system, bsid_book, books_revision[], army{options[…]}}`. The `options` tree is BattleScribe-shaped: `option_id`/`link_id` are BS entry/link ids, `amount` is selection count, `catalogue_id`/`typeId` where relevant. **No per-selection costs** — only roster-level `totalCosts`, so NR-sourced specs pin roster totals only.
- A real sample (`war horde`, 950 pts, Orks) is committed as converter test data.
- Link rot is real: lists get deleted/expire. Hence the snapshot-on-first-evaluation rule.

### `.ros` / `.rosz`

Parsed by wham's existing `Workspaces.BattleScribe` into `RosterNode`. Stores per-selection costs → richer pins than NR (roster totals + per-selection costs). Attachment URLs must match GitHub's user-attachments host patterns.

### Inline YAML steps

Fixture-DSL steps pasted in the form (power users / data authors). Already assertion-bearing; no conversion, no pinning step.

## 4. Components

### muster CLI

- **`muster convert <input> [--pin-observed] [-o <file>]`** — input: `.ros`/`.rosz` path, NR list JSON path, or NR share URL (triggers fetch). Output: fixture-DSL YAML. `--pin-observed` adds assertions for the stored/observed values (default for the report flow). Without it: steps only.
- **`muster report --issue-body <file> --data <root>`** — full report pipeline: parse form/NR body → obtain roster → convert → evaluate → emit (a) markdown reply, (b) verdict + suggested labels, (c) machine JSON, (d) generated spec. Exit 0 = reply produced (any verdict, including needs-info); exit 2 = harness error. Verdict is data, not exit code.
- **`muster promote --issue-body <file> --comments <file> --data <root>`** — locate the newest snapshot block in the harness's own comments, re-evaluate against current data, re-pin assertions to current values, write `tests/rosters/<slug>.yaml`. Branch/PR mechanics are workflow-side.
- **`muster diff --fail-on-broke`** (muster#4) — exit 1 when any row classifies `broke` or `verdict-changed` **in the governing engine** (§5); non-governing breaks surface as `engine-gap`, not check failures. CLI default remains report-don't-judge (flag opt-in).
- **`--engines <list>`** on `test`, `diff`, and `report` — engine names from the registry (default: all available). See §5.

New sources: `Muster.Cli/Reports/` (form parsing, verdict mapping, markdown rendering), `Muster.Cli/Converters/` (NR JSON → spec, RosterNode → spec, shared step-emitter), `Muster.Cli/NewRecruit/` (fetcher). Converters are pure model traversal — **no new wham work identified**. Possible small additive gap in battlescribe-spec if `expectedState` can't assert roster-level total costs (verify at plan time; if missing, extend the DSL additively in the spec repo, as with `HarnessError`).

### Conversion mapping

NR `army.options` tree / `RosterNode` forces+selections →

| Source | DSL step |
|---|---|
| force node | `addForce` (`forceEntryId`), nested via `addChildForce` |
| selection node | `selectEntry` / `selectChildEntry` (by `option_id`; `link_id` when present) |
| `amount` ≠ 1 | `setSelectionCount` |
| custom name | `setCustomization` |
| observed roster totals (`totalCosts`) | `expectedState` roster cost assertions |
| observed per-selection costs (`.ros` only) | `expectedState` selection cost assertions |

Unmappable nodes (unknown ids, unsupported structures) are reported **loudly in the reply** ("selection X could not be mapped — id not found in current data"). Rule: if any step or assertion cannot be emitted, the verdict is `needs-info` — a partially replayed roster must never produce `confirmed`/`not-reproducible`. Note: an id missing from *current* data can itself be the bug (entry deleted); the reply names the missing ids so a maintainer can judge.

### GitHub kit (lives in WarHub/muster)

- **Issue form template** `report-a-data-bug.yml`: roster field (link, attachment, or inline YAML), free-text expected/actual, system/book dropdown optional. Copyable into data repos; docs page explains install.
- **Reusable workflow** `report-check.yml` (`workflow_call`, wired to `issues: [opened, edited]` and `issue_comment: [created]` in the data repo's thin caller):
  - issue events → `muster report` → sticky comment + labels (`issues: write`).
  - `engines` input (default: all available) and `governing` input (default `newrecruit > battlescribe > wham`); WarHub-hosted callers reach the private adapters via the bot app token. Also `fail-on-broke` (default true) and `fail-on-inconclusive` (default false) on the PR-check workflow.
  - `/muster check` comment → re-run evaluation (anyone).
  - `/muster promote` comment → permission check (commenter has write access, via collaborators API) → `muster promote` → branch `muster/promote-issue-<N>` → PR opened linking the issue.
  - NR auto-report recognition: body regex for the `**List:** <url>` pattern, no form required.
- Labels are created idempotently if missing.

## 5. Per-engine evaluation

The battlescribe-spec machinery already supports this — the DSL has per-engine `engines:` expectation blocks, the Runner takes `--assertion-engine`, adapters speak the NDJSON protocol, and `--matrix` merges per-engine conformance reports. Muster orchestrates; it invents no comparison machinery.

### Engine registry

Muster config (`muster.yml` in the data repo, or CLI/Action input) maps engine names to launch commands:

- `newrecruit` — the ecosystem's **current main app** and the default governing reference: what reporters and players actually see. Live Playwright adapter (battlescribe-spec's `BattleScribeSpec.NewRecruit`); heavier than in-proc engines but evaluating one generated spec is a single browser session — acceptable for issue replies, costly for full fixture sweeps (repos tune the engine set per command).
- `battlescribe` — the **legacy oracle**: battlescribe-spec's `ReferenceAdapter` (IKVM-wrapped proprietary BattleScribe JARs). Historical reference semantics; second in default precedence.
- `wham` — builtin, in-proc via `SpecRosterEngineAdapter`; always available and the only engine guaranteed in the public image. **Not yet compliant enough to be the assertion reference** — it informs, catches regressions cheaply, and its divergences from the governor feed its own conformance backlog.

Adapter commands are registered as `dotnet:<path>`, any executable, or `docker:<image>` (runs `docker run -i --rm <image>`; the NDJSON protocol flows over stdio unchanged), spawned per run via TestKit's `AdapterProcess`.

**NR adapter distribution (decided):** `BattleScribeSpec.NewRecruit` ships as a **public Docker image** (Playwright + browser baked in), published by battlescribe-spec's CI the same way muster publishes its own image — binaries only, no repo sources, and no JAR encumbrance exists for this adapter. CI callers register it as `docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest`; GitHub's container-action Docker-socket mount makes this reachable from the muster Action (verify the socket path during implementation; fall back to running the adapter as a workflow service container if not). The `battlescribe` oracle can never ship this way — its image would redistribute the proprietary JARs.

Availability detection: an engine is *available* when builtin or its adapter command resolves and starts. Requested-but-unavailable engines appear in output as `unavailable` — named, never silently dropped. Default engine set: all available.

**Licensing boundary (hard):** nothing in muster's public artifacts may embed or download the proprietary BattleScribe JARs — the `battlescribe` adapter exists only where the private battlescribe-spec checkout is present (WarHub-hosted workflows via the bot app token; maintainers with access, locally). The NR adapter has no JAR encumbrance, only private-repo residency, and ships as a public Docker image (above); the muster image itself still guarantees wham only.

### Governing-engine rule

One engine **governs** each verdict; the rest inform. Precedence is **configurable** (`engines.governing` in `muster.yml` / Action input), default `newrecruit > battlescribe > wham`: the governor is the first engine in precedence that actually ran. Rationale: NR is what the community currently plays with, so "confirmed" should mean "the main app still shows this today"; legacy BS carries reference semantics when NR can't run; wham governs only as a last resort and the reply says so explicitly. Whenever engines that ran disagree with each other on any asserted value, `engine-gap` is raised alongside the governing verdict — abstain-don't-lie extended to multi-engine: wham never silently masquerades as the reference, and every wham-vs-governor divergence auto-feeds the wham conformance backlog (the harness keeps finishing wham).

### Command semantics

- **`report`:** the reply matrix is *value × engine* — the **NR-reported** column (the roster's stored values from report time; an observation, never the governor) then one column per engine that ran now. Verdict per the governing-engine rule; with NR governing, `confirmed` literally means "New Recruit still shows this against latest data".
- **`test`:** per-fixture, per-engine pass/fail/inconclusive; a fixture passes when every ran engine matches its engine-resolved expectations (DSL `engines:` blocks apply). Exit 1 on any engine's assertion failure; abstention stays per-engine inconclusive.
- **`diff`:** blast radius classified **per engine** — each fixture row carries base→head classification for every engine that ran in both states (e.g. `wham: pass→fail broke ❌ · newrecruit: pass→pass unchanged` — that combination signals a wham gap, not a data break). The comment table shows one column per engine; `--fail-on-broke` trips on `broke`/`verdict-changed` in the **governing engine**; non-governing breaks are surfaced but gate only the `engine-gap` signal, not the check. Engines available in only one of base/head are reported `unavailable`, excluded from gating.
- **JSON schema:** `RunReport` gains an engine dimension on every result (additive; single-engine output remains the degenerate case).

### Performance note

Adapter engines run out-of-proc per fixture over NDJSON; total work scales fixtures × engines (× 2 for diff). The per-fixture time budget applies per engine, and the report names any engine that blew it (that engine goes inconclusive; others still count).

## 6. Error handling (hostile input by default)

- Issue bodies are stranger-controlled. Fetch allowlist: exactly `https://www.newrecruit.eu/app/list/<[A-Za-z0-9]+>` and GitHub user-attachment URLs; nothing else is ever fetched.
- Caps: fetch timeout, response size cap, JSON depth/node-count caps, roster selection-count cap. Exceeding any → `needs-info` reply naming the limit.
- No stack traces or raw exceptions in replies — harness errors exit 2 and the workflow posts a generic "harness error, maintainers notified" comment (`::error::` in the log carries details).
- Every reply names which engines ran, which governed the verdict, and which were unavailable. Reply also shows `books_revision` the reporter used vs. data evaluated.

## 7. Testing

- **Unit:** form/body parser (adversarial: injection attempts, malformed markdown, huge bodies, wrong URLs); NR JSON converter against the committed `war horde` sample; RosterNode converter; verdict mapping incl. governing-engine rule and `engine-gap`; URL allowlist; engine-registry availability detection (missing adapter → `unavailable`, not crash).
- **Integration:** generated specs execute through the FakeEngine end-to-end; a second, deliberately-divergent FakeEngine exercises multi-engine runs (matrix rendering, `engine-gap`, per-engine diff classification); `--fail-on-broke` exit codes across engines; promote round-trip (snapshot → re-pin → fixture file).
- **E2E on the pilot fork (milestone exit):** hand-file an NR-style issue (clone of BSData#234 content with a live list) → `confirmed`; fix the data → `/muster check` → `not-reproducible`; `/muster promote` → fixture PR opened. Three beats, screenshot each.

## 8. Out of scope (this cycle)

Remaining hardening backlog: per-fixture setup perf (shared compilation cache), wham#310 fix, engine evaluation of `ModifierKind.Replace/Ceil/Floor` + `ConditionGroupKind.Count`, associations/alias round-trip — next cycle. M4 narrows: per-engine evaluation lands the shadow-compare *mechanism* now, so M4 keeps only the capability manifest and the nightly schedule/trend reporting. NR stable-API collaboration is an M5 conversation, not code.

## Open questions (tracked, not blocking)

1. Does battlescribe-spec `expectedState` support roster-level total-cost assertions today? Verify at plan time; extend additively if not.
2. `.rosz` attachment friction: GitHub issue attachments may reject the extension (zip rename workaround) — document in the form's help text; verify while building the template.
3. Promotion slug collisions (`tests/rosters/<slug>.yaml` exists) — suffix with issue number; confirm during implementation.
4. ~~NR adapter distribution~~ — resolved: public Docker image published from battlescribe-spec CI (see §5); non-WarHub repos get the default governor by registering `docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest`.
5. NR adapter runtime cost in CI — the prebuilt image removes Playwright install cost; browser-session cost per spec remains. Measure during implementation; full-sweep `test`/`diff` with NR may need an opt-in or fixture-count guard.
