#!/usr/bin/env bash
set -euo pipefail

# muster GitHub Action entrypoint.
#
# Usage:
#   entrypoint.sh <data-path> <fixtures-path> [base-ref] [fail-on-broke] [fail-on-inconclusive] [engines] [governing]
#   entrypoint.sh report <data-path> <issue-body-file> <data-source> [engines] [governing] [out-dir]
#   entrypoint.sh promote <data-path> <issue-body-file> <comments-file> <data-source> <issue-number> [out-dir] [engines] [governing]
#
# --- positional-args form (no mode word) ---
#
# Unchanged, backward compatible with the published `docker://ghcr.io/warhub/muster:latest`
# Action (action.yml), which never passes a mode word as its first argument:
#
#   data-path             Data repo root (checkout-relative or absolute).
#   fixtures-path         Golden fixtures directory (checkout-relative or absolute).
#   base-ref              Git ref to diff against. Empty (or omitted) -> `muster test`.
#                          Non-empty -> `muster diff --base <base-ref> --head <workspace>`.
#   fail-on-broke         "true"/"false" (default: true). Passes `--fail-on-broke` to
#                          `muster diff` when true; ignored in `muster test` mode.
#   fail-on-inconclusive  "true"/"false" (default: false). When true, an inconclusive
#                          (exit 2) muster run fails the check instead of warning neutrally.
#   engines                Space-separated engine specs, forwarded as `--engines ...` to
#                          both `muster test` and `muster diff` (default: "wham").
#   governing              Space-separated governing precedence, forwarded as
#                          `--governing ...` (default: "newrecruit battlescribe wham").
#
# --- `report` mode ---
#
# Used by the reusable .github/workflows/report-check.yml workflow's `evaluate` job (M3):
# an issue is opened/edited, or someone comments `/muster check`.
#
#   data-path              Data repo root.
#   issue-body-file        Path to a file containing the raw GitHub issue body text.
#   data-source            dataSource URI the generated spec should target, e.g.
#                          "github:BSData/wh40k-11e[@ref]" — also seeds the dataroot cache
#                          layout (see build_dataroot_for_source below).
#   engines                (optional, default "wham")
#   governing              (optional, default "newrecruit battlescribe wham")
#   out-dir                (optional, default ".") — reply.md/report.json/snapshot.yaml land here.
#
# Exit code: 0 always, EXCEPT a genuine harness error (muster report exit 2, meaning no
# usable reply was produced at all) is surfaced as `::error::` + exit 1 — unlike test/diff
# mode's exit-2 handling below, report mode never treats exit 2 as a neutral pass, because a
# reporter is waiting on a reply that never got written.
#
# --- `promote` mode ---
#
# Used by the reusable .github/workflows/report-check.yml workflow's `promote` job (M3):
# a write-permission commenter comments `/muster promote`.
#
#   data-path              Data repo root.
#   issue-body-file        Path to a file containing the raw GitHub issue body text.
#   comments-file          Path to `gh api .../issues/<n>/comments --paginate` JSON output.
#   data-source            Same dataSource URI as report mode (seeds the dataroot layout).
#   issue-number           GitHub issue number.
#   out-dir                (optional, default "tests/rosters")
#   engines                (optional, default "wham")
#   governing              (optional, default "newrecruit battlescribe wham")
#
# MUSTER_CMD (default: "dotnet /app/muster.dll") is the muster invocation,
# split on whitespace. This lets the same script run inside the prebuilt
# container image (MUSTER_CMD unset -> dotnet /app/muster.dll) and locally
# against a `dotnet publish` output (MUSTER_CMD="dotnet <publish-dir>/muster.dll").
#
# Fixtures declare `setup.dataSource: github:{org}/{repo}[@{ref}]`, which
# muster's DataSourceResolver expects to find under
# <dataroot>/github/{org}/{repo}/{ref-or-latest}/. In a fork's CI run,
# GITHUB_REPOSITORY names the fork/consumer repo, not the org/repo baked
# into the fixture -- so this script derives the dataroot layout from the
# fixtures themselves (grep for `dataSource: github:...` declarations, test/diff
# modes) or from an explicit `data-source` argument (report/promote modes), rather
# than from GITHUB_REPOSITORY, and populates each derived
# github/{org}/{repo}/{ref}/ directory with only the TOP-LEVEL data files
# (*.yaml/*.yml/*.cat/*.gst) from data-path. It never recurses into
# data-path, so a fixtures/tests subdirectory living under data-path is
# never swept into the dataroot (muster's fixture-discovery file walk is
# recursive and would choke on fixture YAML mixed into game data).

MUSTER_CMD="${MUSTER_CMD:-dotnet /app/muster.dll}"
read -ra MUSTER_CMD_ARR <<< "$MUSTER_CMD"

# Docker container actions run as root against a mounted checkout owned by
# a different uid; `git worktree`/`git -C` refuse to operate on such repos
# without this. Harmless (and idempotent) when running locally too.
git config --global --add safe.directory '*'

# Populates dataroot "$1" from data directory "$2" for a SINGLE
# `github:{org}/{repo}[@{ref}]` dataSource URI "$3": creates
# $1/github/{org}/{repo}/{ref-or-latest}/ and copies the top-level
# *.yaml/*.yml/*.cat/*.gst files from "$2" into it. Shared cache-layout logic used by both
# `build_dataroot` below (test/diff modes: source(s) scanned out of fixture declarations)
# and report/promote modes (source given directly as a CLI argument, no fixtures dir to scan).
build_dataroot_for_source() {
    local dataroot="$1"
    local data_path="$2"
    local uri="$3"

    if [[ "$uri" != github:* ]]; then
        echo "::error::muster: unsupported data-source '$uri' (only 'github:{org}/{repo}[@{ref}]' is supported)" >&2
        return 1
    fi

    local rest org repo_and_ref repo ref dest
    rest="${uri#github:}"
    org="${rest%%/*}"
    repo_and_ref="${rest#*/}"
    if [[ "$repo_and_ref" == *@* ]]; then
        repo="${repo_and_ref%@*}"
        ref="${repo_and_ref##*@}"
    else
        repo="$repo_and_ref"
        ref="latest"
    fi

    dest="$dataroot/github/$org/$repo/$ref"
    mkdir -p "$dest"
    find "$data_path" -maxdepth 1 -type f \
        \( -name '*.yaml' -o -name '*.yml' -o -name '*.cat' -o -name '*.gst' \) \
        -exec cp -t "$dest" {} +
}

MODE="${1:-}"

if [[ "$MODE" == "report" ]]; then
    shift
    if [[ $# -lt 3 ]]; then
        echo "usage: entrypoint.sh report <data-path> <issue-body-file> <data-source> [engines] [governing] [out-dir]" >&2
        exit 2
    fi

    DATA_PATH="$1"
    BODY_FILE="$2"
    DATA_SOURCE="$3"
    ENGINES_INPUT="${4:-wham}"
    GOVERNING_INPUT="${5:-newrecruit battlescribe wham}"
    OUT_DIR="${6:-.}"

    ENGINE_ARGS=(--engines)
    read -r -a ENGINE_LIST <<< "$ENGINES_INPUT"
    ENGINE_ARGS+=("${ENGINE_LIST[@]}")
    GOVERNING_ARGS=(--governing)
    read -r -a GOVERNING_LIST <<< "$GOVERNING_INPUT"
    GOVERNING_ARGS+=("${GOVERNING_LIST[@]}")

    DATA_PATH_ABS="$(cd "$DATA_PATH" && pwd)"
    REPORT_DATAROOT="$(mktemp -d)"
    trap 'rm -rf "$REPORT_DATAROOT"' EXIT

    build_dataroot_for_source "$REPORT_DATAROOT" "$DATA_PATH_ABS" "$DATA_SOURCE"

    rc=0
    "${MUSTER_CMD_ARR[@]}" report \
        --issue-body "$BODY_FILE" \
        --data "$REPORT_DATAROOT" \
        --data-source "$DATA_SOURCE" \
        --out-dir "$OUT_DIR" \
        "${ENGINE_ARGS[@]}" "${GOVERNING_ARGS[@]}" || rc=$?

    if [[ "$rc" -ne 0 ]]; then
        echo "::error::muster report harness error (exit $rc) -- see log above" >&2
        exit 1
    fi
    exit 0
fi

if [[ "$MODE" == "promote" ]]; then
    shift
    if [[ $# -lt 5 ]]; then
        echo "usage: entrypoint.sh promote <data-path> <issue-body-file> <comments-file> <data-source> <issue-number> [out-dir] [engines] [governing]" >&2
        exit 2
    fi

    DATA_PATH="$1"
    BODY_FILE="$2"
    COMMENTS_FILE="$3"
    DATA_SOURCE="$4"
    ISSUE_NUMBER="$5"
    OUT_DIR="${6:-tests/rosters}"
    ENGINES_INPUT="${7:-wham}"
    GOVERNING_INPUT="${8:-newrecruit battlescribe wham}"

    ENGINE_ARGS=(--engines)
    read -r -a ENGINE_LIST <<< "$ENGINES_INPUT"
    ENGINE_ARGS+=("${ENGINE_LIST[@]}")
    GOVERNING_ARGS=(--governing)
    read -r -a GOVERNING_LIST <<< "$GOVERNING_INPUT"
    GOVERNING_ARGS+=("${GOVERNING_LIST[@]}")

    DATA_PATH_ABS="$(cd "$DATA_PATH" && pwd)"
    PROMOTE_DATAROOT="$(mktemp -d)"
    trap 'rm -rf "$PROMOTE_DATAROOT"' EXIT

    build_dataroot_for_source "$PROMOTE_DATAROOT" "$DATA_PATH_ABS" "$DATA_SOURCE"

    rc=0
    "${MUSTER_CMD_ARR[@]}" promote \
        --issue-body "$BODY_FILE" \
        --comments "$COMMENTS_FILE" \
        --data "$PROMOTE_DATAROOT" \
        --issue-number "$ISSUE_NUMBER" \
        --out "$OUT_DIR" \
        "${ENGINE_ARGS[@]}" "${GOVERNING_ARGS[@]}" || rc=$?

    if [[ "$rc" -ne 0 ]]; then
        echo "::error::muster promote failed (exit $rc) -- see log above" >&2
        exit 1
    fi
    exit 0
fi

# --- existing test/diff flow (backward compatible: $1 is always data-path here, never a
#     mode word -- the published Action never passes one) ---

if [[ $# -lt 2 ]]; then
    echo "usage: entrypoint.sh <data-path> <fixtures-path> [base-ref]" >&2
    exit 2
fi

DATA_PATH="$1"
FIXTURES_PATH="$2"
BASE_REF="${3:-}"
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

REPORT_MD="muster-report.md"
REPORT_JSON="muster-report.json"

DATA_PATH_ABS="$(cd "$DATA_PATH" && pwd)"
FIXTURES_PATH_ABS="$(cd "$FIXTURES_PATH" && pwd)"

# Populates dataroot "$1" from data directory "$2": for every distinct
# `dataSource: github:{org}/{repo}[@{ref}]` declaration found under
# $FIXTURES_PATH_ABS, delegate to build_dataroot_for_source for the actual
# github/{org}/{repo}/{ref}/ layout + copy.
build_dataroot() {
    local dataroot="$1"
    local data_path="$2"
    local -a sources

    mapfile -t sources < <(
        grep -rhoE 'dataSource:[[:space:]]*"?github:[^"[:space:]]+' "$FIXTURES_PATH_ABS" 2>/dev/null \
            | sed -E 's/.*(github:[^"[:space:]]+).*/\1/' \
            | sort -u || true
    )

    if [[ ${#sources[@]} -eq 0 ]]; then
        echo "::warning::muster: no 'dataSource: github:...' declarations found under $FIXTURES_PATH_ABS" >&2
        return 0
    fi

    local uri
    for uri in "${sources[@]}"; do
        build_dataroot_for_source "$dataroot" "$data_path" "$uri"
    done
}

HEAD_DATAROOT="$(mktemp -d)"
BASE_DATAROOT=""
WORKTREE_DIR=""
REPO_ROOT=""

cleanup() {
    rm -rf "$HEAD_DATAROOT"
    if [[ -n "$BASE_DATAROOT" ]]; then
        rm -rf "$BASE_DATAROOT"
    fi
    if [[ -n "$WORKTREE_DIR" && -n "$REPO_ROOT" ]]; then
        git -C "$REPO_ROOT" worktree remove --force "$WORKTREE_DIR" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

build_dataroot "$HEAD_DATAROOT" "$DATA_PATH_ABS"

rc=0
if [[ -z "$BASE_REF" ]]; then
    if "${MUSTER_CMD_ARR[@]}" test \
        --data "$HEAD_DATAROOT" \
        --fixtures "$FIXTURES_PATH_ABS" \
        --output github-actions \
        --report "$REPORT_JSON" \
        "${ENGINE_ARGS[@]}" "${GOVERNING_ARGS[@]}" > "$REPORT_MD"; then
        rc=0
    else
        rc=$?
    fi
else
    # Normalize through `cd`+`pwd`: on Windows/Git Bash, `git rev-parse
    # --show-toplevel` prints a "D:/..." path while `pwd` (used above for
    # DATA_PATH_ABS) prints the msys "/d/..." form. realpath's
    # --relative-to needs both operands in the same representation, so
    # re-resolve the toplevel through the shell's own path handling
    # instead of using git's string verbatim.
    REPO_ROOT="$(cd "$(git -C "$DATA_PATH_ABS" rev-parse --show-toplevel)" && pwd)"
    REL_DATA_PATH="$(realpath --relative-to="$REPO_ROOT" "$DATA_PATH_ABS")"

    WORKTREE_DIR="$(mktemp -d)"
    rmdir "$WORKTREE_DIR"
    git -C "$REPO_ROOT" worktree add "$WORKTREE_DIR" "$BASE_REF"

    BASE_DATAROOT="$(mktemp -d)"
    build_dataroot "$BASE_DATAROOT" "$WORKTREE_DIR/$REL_DATA_PATH"

    DIFF_ARGS=(diff \
        --base "$BASE_DATAROOT" \
        --head "$HEAD_DATAROOT" \
        --fixtures "$FIXTURES_PATH_ABS" \
        --output markdown \
        "${ENGINE_ARGS[@]}" "${GOVERNING_ARGS[@]}")
    if [[ "$FAIL_ON_BROKE" == "true" ]]; then
        DIFF_ARGS+=(--fail-on-broke)
    fi

    if "${MUSTER_CMD_ARR[@]}" "${DIFF_ARGS[@]}" > "$REPORT_MD"; then
        rc=0
    else
        rc=$?
    fi
fi

cat "$REPORT_MD" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "report-path=$(pwd)/$REPORT_MD" >> "$GITHUB_OUTPUT"
fi

if [[ "$rc" -eq 2 ]]; then
    if [[ "$FAIL_ON_INCONCLUSIVE" == "true" ]]; then
        echo "::error::muster run was inconclusive (exit 2) and fail-on-inconclusive is set"
        exit 1
    fi
    echo "::warning::muster run was inconclusive (exit 2) -- treating as neutral"
    exit 0
fi

exit "$rc"
