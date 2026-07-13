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

## 1. Product contract

A data repo that already uses the muster Action gains a second install: an issue form ("Report a data bug") plus a reusable workflow. The flow:

1. **Report.** A reporter files the issue form (or New Recruit auto-files its own report — recognized too). The body carries a roster as one of: NR share link, `.ros`/`.rosz` attachment URL, or inline fixture-DSL YAML steps.
2. **Evaluate.** The workflow runs the muster container. Muster parses the body, obtains the roster, converts it to a fixture-DSL spec whose assertions **pin the observed values** (what the reporter's app showed), and evaluates it against latest data with the wham engine.
3. **Label + reply.** Sticky comment with the verdict and the engine's actual values; label applied:
   - `confirmed` — assertions pass: latest data still produces what the reporter saw. The bug reproduces.
   - `not-reproducible` — assertions fail: values changed (often "already fixed"); reply shows the exact diffs.
   - `needs-info` — unusable input (bad link, rotted list, unparseable roster). Polite reply, never a stack trace.
   - `inconclusive` — engine abstained (M2 discipline unchanged): visible, honest, never wrong.
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
- **`muster diff --fail-on-broke`** (muster#4) — exit 1 when any row classifies `broke` or `verdict-changed`. CLI default remains report-don't-judge (flag opt-in).

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
  - `/muster check` comment → re-run evaluation (anyone).
  - `/muster promote` comment → permission check (commenter has write access, via collaborators API) → `muster promote` → branch `muster/promote-issue-<N>` → PR opened linking the issue.
  - NR auto-report recognition: body regex for the `**List:** <url>` pattern, no form required.
- Labels are created idempotently if missing.

## 5. Error handling (hostile input by default)

- Issue bodies are stranger-controlled. Fetch allowlist: exactly `https://www.newrecruit.eu/app/list/<[A-Za-z0-9]+>` and GitHub user-attachment URLs; nothing else is ever fetched.
- Caps: fetch timeout, response size cap, JSON depth/node-count caps, roster selection-count cap. Exceeding any → `needs-info` reply naming the limit.
- No stack traces or raw exceptions in replies — harness errors exit 2 and the workflow posts a generic "harness error, maintainers notified" comment (`::error::` in the log carries details).
- Engine caveat stated in every reply: values are computed by the wham engine; NR's own engine occasionally diverges from BattleScribe semantics. Reply also shows `books_revision` the reporter used vs. data evaluated.

## 6. Testing

- **Unit:** form/body parser (adversarial: injection attempts, malformed markdown, huge bodies, wrong URLs); NR JSON converter against the committed `war horde` sample; RosterNode converter; verdict mapping; URL allowlist.
- **Integration:** generated specs execute through the FakeEngine end-to-end; `--fail-on-broke` exit codes; promote round-trip (snapshot → re-pin → fixture file).
- **E2E on the pilot fork (milestone exit):** hand-file an NR-style issue (clone of BSData#234 content with a live list) → `confirmed`; fix the data → `/muster check` → `not-reproducible`; `/muster promote` → fixture PR opened. Three beats, screenshot each.

## 7. Out of scope (this cycle)

Remaining hardening backlog: per-fixture setup perf (shared compilation cache), wham#310 fix, engine evaluation of `ModifierKind.Replace/Ceil/Floor` + `ConditionGroupKind.Count`, associations/alias round-trip — next cycle. M4 trust machinery (capability manifest, nightly shadow-compare) unchanged. NR stable-API collaboration is an M5 conversation, not code.

## Open questions (tracked, not blocking)

1. Does battlescribe-spec `expectedState` support roster-level total-cost assertions today? Verify at plan time; extend additively if not.
2. `.rosz` attachment friction: GitHub issue attachments may reject the extension (zip rename workaround) — document in the form's help text; verify while building the template.
3. Promotion slug collisions (`tests/rosters/<slug>.yaml` exists) — suffix with issue number; confirm during implementation.
