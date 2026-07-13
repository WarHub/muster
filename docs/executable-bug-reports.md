# Executable bug reports

Muster's second install turns a data repo's bug reports into reproductions and, once fixed,
into permanent regression fixtures. A reporter files an issue (or New Recruit auto-files one
for them); muster evaluates the reported roster against current data with every configured
engine, replies with a verdict and a per-engine matrix, and labels the issue. Once a
maintainer fixes the underlying data, `/muster promote` turns the same report into a golden
fixture under `tests/rosters/` — a bug becomes a test, and the correctness review happens
exactly once, on that PR.

This is a second, separate install from the [blast-radius CI check](authoring-fixtures.md)
(`action.yml` / `muster test` / `muster diff`) — you can use one without the other, but they
share the same fixture format, so most repos will want both.

## Install (2 files)

Copy these two files from [WarHub/muster](https://github.com/WarHub/muster) into your data
repo, no forking or vendoring required:

1. **`kit/issue-form/report-a-data-bug.yml`** → `.github/ISSUE_TEMPLATE/report-a-data-bug.yml`

   The issue form reporters fill in. Its `Roster`/`Problem`/`Expected` field labels are
   load-bearing — muster's body parser (`IssueBody.Parse`) matches GitHub's rendered
   `### Problem` / `### Expected` headings by exact label text. If you customize the form,
   keep those three field labels as-is (or don't rename `problem`/`expected`'s **label**
   text; the field `id`s can change freely).

2. **`kit/callers/muster-report.yml`** → `.github/workflows/muster-report.yml`

   A thin caller that wires GitHub's `issues`/`issue_comment` events to muster's reusable
   workflow (`WarHub/muster/.github/workflows/report-check.yml@main`) and sets your repo's
   `data-source`:

   ```yaml
   jobs:
     report:
       uses: WarHub/muster/.github/workflows/report-check.yml@main
       with:
         data-source: "github:YourOrg/your-data-repo" # ← your org/repo[@ref]
   ```

   `data-source` must match what your fixtures declare under `setup.dataSource` (see
   [authoring-fixtures.md](authoring-fixtures.md)) — it seeds the same
   `github/{org}/{repo}/{ref-or-latest}/` cache layout `muster test`/`muster diff` use, built
   fresh from your repo's own checkout each run (never a live clone).

No secrets to configure: the reusable workflow uses the ambient `GITHUB_TOKEN`
(`github.token`) for comments, labels, and PRs. The five labels
(`confirmed`/`not-reproducible`/`needs-info`/`inconclusive`/`engine-gap`) are created
idempotently on first run — nothing to pre-provision.

## Roster formats

The issue form (and NR's own auto-filed reports) accept a roster in any of three shapes;
muster's body parser tries them in this order and uses the first match:

| Format | How it's recognized | Notes |
|---|---|---|
| **New Recruit share link** | `https://www.newrecruit.eu/app/list/<key>` anywhere in the body | Fetched live (undocumented NR RPC, allowlisted to this exact host/path — any failure degrades to `needs-info`, never a crash). NR only exposes roster-level `totalCosts`, not per-selection costs. Link rot is expected — once evaluated, the reply's snapshot is the durable copy. |
| **`.ros`/`.rosz` attachment** | a GitHub `user-attachments`/legacy `owner/repo/files` URL ending in `.ros`/`.rosz`/`.zip` | Richer than NR: stores per-selection costs, not just roster totals. GitHub sometimes forces a `.zip` rename on upload — the issue form's help text tells reporters to rename it back. |
| **Inline fixture-DSL YAML** | a fenced `` ```yaml `` block containing a `steps:` list | For power users / data authors. The pasted YAML *is* the spec — no conversion or value-pinning step; author it exactly like a fixture in `tests/rosters/` (see [authoring-fixtures.md](authoring-fixtures.md)), including `id`/`setup.dataSource`/assertions. |

Whichever format is used, muster converts (or, for inline YAML, validates) it into a fixture-DSL
spec whose assertions **pin the observed values** — what the reporter's app showed — and
evaluates that spec against current data. If any node can't be mapped (unknown id, unsupported
structure), the verdict is always `needs-info`, naming the unmapped id — a partial replay is
never allowed to produce `confirmed`/`not-reproducible`.

## Verdict and label semantics

One label from the first group is always applied; `engine-gap` is added alongside it when the
engines that ran disagree with each other:

| Label | Verdict | Meaning |
|---|---|---|
| `confirmed` | Confirmed | The governing engine reproduces the reported values against current data — the bug is real (or still present). |
| `not-reproducible` | Not reproducible | The governing engine computes different values than reported — often means "already fixed"; the reply shows the exact per-engine diff. |
| `needs-info` | Needs info | Unusable input: no roster found, a bad/rotted link, or a roster that couldn't be fully mapped to current data. Polite reply, never a stack trace. |
| `inconclusive` | Inconclusive | Every requested engine was unavailable (couldn't start) — never silently treated as any other verdict. |
| `engine-gap` | *(additive)* | The engines that ran disagree with each other on the asserted values, independent of the governing verdict — surfaced for the wham conformance backlog, not itself a pass/fail signal. |

The reply also names which engines ran, which one governed the verdict, and which requested
engines were unavailable — and embeds the generated spec in a collapsed
`<details><summary>Executable spec (snapshot)</summary>` block (marked internally with
`<!-- muster:snapshot -->`). That snapshot is what `/muster promote` reads back later.

## Commands

Both are issue *comments*; the reusable workflow's `evaluate`/`promote` jobs match them by
substring, so exact comment text (`/muster check`, `/muster promote`) matters, not exact
match.

- **`/muster check`** — anyone can comment this to force a re-evaluation (e.g. after data or
  the report itself changed). Same flow as the initial issue-opened/edited trigger: re-parses
  the current issue body, re-runs every engine, updates the sticky reply comment
  (`<!-- muster:report -->`) and labels in place.

- **`/muster promote`** — **write-access gated**: the workflow checks the commenter's
  permission via the collaborators API (`admin`/`write`/`maintain` only) before doing
  anything; anyone else gets a polite refusal comment and the job fails closed. On success it:
  1. re-reads the newest `<!-- muster:snapshot -->` block from the issue's own comments,
  2. re-evaluates it against current data with the governing engine,
  3. re-pins the spec's assertions to the engine's **current** (post-fix) values,
  4. opens a PR (`muster/promote-issue-<N>`) adding `tests/rosters/report-issue-<N>.yaml`
     (suffixed `-2`, `-3`, … on a slug collision) and linking `Closes #<N>`.

  Human correctness review happens exactly once, on that PR — reviewing the pinned expected
  values is reviewing "is this actually correct now," same as any fixture.

## Promotion PRs and CI checks

By default, the promotion PR is opened with `GITHUB_TOKEN` and GitHub will not run workflows on it
(a documented GitHub Actions limitation). If you want the promotion PR to automatically trigger
your CI checks, provide a PAT (personal access token) or GitHub App installation token as the
optional `pr-token` secret:

```yaml
jobs:
  report:
    uses: WarHub/muster/.github/workflows/report-check.yml@main
    with:
      data-source: "github:YourOrg/your-data-repo"
    secrets:
      pr-token: ${{ secrets.MUSTER_PR_TOKEN }}
```

Without this token, you can still run workflows manually by closing and re-opening the PR or by
pushing to its branch.

## Engines and governing precedence

Both the `evaluate` and `promote` jobs accept `engines`/`governing` inputs on the reusable
workflow (same registry and syntax as `muster test`/`muster diff` — see
[authoring-fixtures.md](authoring-fixtures.md) and the CLI's `--engines`/`--governing`
help text):

```yaml
jobs:
  report:
    uses: WarHub/muster/.github/workflows/report-check.yml@main
    with:
      data-source: "github:YourOrg/your-data-repo"
      engines: "wham newrecruit=docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest"
      governing: "newrecruit battlescribe wham"    # default; first-in-precedence engine that ran governs
      fixtures-out: "tests/rosters"                 # default; where /muster promote writes fixtures
```

- **`wham`** — builtin, in-process, always available; the only engine guaranteed in the
  public `ghcr.io/warhub/muster` image. Registered by name alone (no `=command`).
- **`name=dotnet:<path.dll>`**, **`name=exe [args]`**, **`name=docker:<image>`** — any other
  engine is an out-of-process adapter speaking the NDJSON protocol over stdio, spawned per
  run. `docker:<image>` runs `docker run -i --rm <image>` under the hood.
- **`governing`** picks the single engine whose result is authoritative for the
  `confirmed`/`not-reproducible` verdict: the first name in the `governing` list that's
  actually among the engines that ran. Default precedence is `newrecruit battlescribe wham` —
  New Recruit (what reporters and players actually use today) governs when available, the
  legacy BattleScribe oracle is the fallback reference, and wham (not yet fully spec-compliant)
  governs only as a last resort — the reply says explicitly which engine governed.
- Requested-but-unavailable engines are never silently dropped — they're named `unavailable`
  in the reply, and if *no* requested engine is available the verdict is `inconclusive`
  (abstain, don't lie).

### Registering the New Recruit adapter (default governor)

To get the default `newrecruit`-governs behavior outside WarHub-hosted workflows, register
the public adapter image as a Docker engine:

```yaml
engines: "wham newrecruit=docker:ghcr.io/warhub/bsspec-adapter-newrecruit:latest"
```

This image ships Playwright + a browser baked in (published by battlescribe-spec's CI) — no
extra setup beyond the `engines` input above. It has no proprietary-JAR encumbrance, unlike
the legacy `battlescribe` oracle (IKVM-wrapped proprietary BattleScribe JARs), which is only
available where a private `battlescribe-spec` checkout exists (WarHub-hosted workflows via the
bot app token, or maintainers with direct access) — it can never ship as a public image, and
non-WarHub repos simply won't have it in their `engines` list.

## Implementation note: why the reusable workflow doesn't run inside the muster container

`ghcr.io/warhub/muster:latest` (see `Dockerfile`) is `dotnet/runtime` + `git` only — no `gh`
CLI, no `python3`/`jq`. `.github/workflows/report-check.yml`'s `evaluate` and `promote` jobs
therefore run on plain `ubuntu-latest` runners (where `gh` and `jq` are preinstalled and
`GITHUB_TOKEN` is ambient) and invoke the muster image explicitly for the `report`/`promote`
steps only, mounting the checkout the same way the container-action (`action.yml`) does:

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace \
  --entrypoint /entrypoint.sh ghcr.io/warhub/muster:latest \
  report <data-path> <issue-body-file> <data-source> <engines> <governing> <out-dir>
```

Every `gh`/label/PR/sticky-comment step runs on the runner itself, outside the container.
