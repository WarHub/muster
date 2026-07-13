#!/usr/bin/env bash
set -euo pipefail

# muster GitHub Action entrypoint.
#
# Usage: entrypoint.sh <data-path> <fixtures-path> [base-ref]
#
#   data-path      Data repo root (checkout-relative or absolute).
#   fixtures-path  Golden fixtures directory (checkout-relative or absolute).
#   base-ref       Git ref to diff against. Empty (or omitted) -> `muster test`.
#                   Non-empty -> `muster diff --base <base-ref> --head <workspace>`.
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
# fixtures themselves (grep for `dataSource: github:...` declarations)
# rather than from GITHUB_REPOSITORY, and populates each derived
# github/{org}/{repo}/{ref}/ directory with only the TOP-LEVEL data files
# (*.yaml/*.yml/*.cat/*.gst) from data-path. It never recurses into
# data-path, so a fixtures/tests subdirectory living under data-path is
# never swept into the dataroot (muster's fixture-discovery file walk is
# recursive and would choke on fixture YAML mixed into game data).

if [[ $# -lt 2 ]]; then
    echo "usage: entrypoint.sh <data-path> <fixtures-path> [base-ref]" >&2
    exit 2
fi

MUSTER_CMD="${MUSTER_CMD:-dotnet /app/muster.dll}"
read -ra MUSTER_CMD_ARR <<< "$MUSTER_CMD"

DATA_PATH="$1"
FIXTURES_PATH="$2"
BASE_REF="${3:-}"

REPORT_MD="muster-report.md"
REPORT_JSON="muster-report.json"

# Docker container actions run as root against a mounted checkout owned by
# a different uid; `git worktree`/`git -C` refuse to operate on such repos
# without this. Harmless (and idempotent) when running locally too.
git config --global --add safe.directory '*'

DATA_PATH_ABS="$(cd "$DATA_PATH" && pwd)"
FIXTURES_PATH_ABS="$(cd "$FIXTURES_PATH" && pwd)"

# Populates dataroot "$1" from data directory "$2": for every distinct
# `dataSource: github:{org}/{repo}[@{ref}]` declaration found under
# $FIXTURES_PATH_ABS, create $1/github/{org}/{repo}/{ref-or-latest}/ and
# copy the top-level *.yaml/*.yml/*.cat/*.gst files from "$2" into it.
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

    local uri rest org repo_and_ref repo ref dest
    for uri in "${sources[@]}"; do
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
        --report "$REPORT_JSON" > "$REPORT_MD"; then
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

    if "${MUSTER_CMD_ARR[@]}" diff \
        --base "$BASE_DATAROOT" \
        --head "$HEAD_DATAROOT" \
        --fixtures "$FIXTURES_PATH_ABS" \
        --output markdown > "$REPORT_MD"; then
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
    echo "::warning::muster run was inconclusive (exit 2) -- treating as neutral"
    exit 0
fi

exit "$rc"
