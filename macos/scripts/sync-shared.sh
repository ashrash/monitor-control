#!/usr/bin/env bash
# Copies the platform-neutral files shared with the Windows app into the Swift package:
#   ../profiles -> Sources/MonitorSwitcherCore/Resources/profiles   (bundled monitor profiles)
#   ../spec     -> Tests/MonitorSwitcherCoreTests/Resources/spec     (golden packet vectors)
# Safe to run repeatedly and from any directory.
set -euo pipefail

MACOS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_DIR="$(cd "$MACOS_DIR/.." && pwd)"

sync_dir() {
    local from="$1" to="$2"
    if [[ ! -d "$from" ]]; then
        echo "error: $from not found" >&2
        exit 1
    fi
    mkdir -p "$to"
    # Remove previous copies but keep the committed README placeholder.
    find "$to" -mindepth 1 ! -name README.md -exec rm -rf {} + 2>/dev/null || true
    (cd "$from" && find . -type f -name '*.json' -print0) | while IFS= read -r -d '' file; do
        mkdir -p "$to/$(dirname "$file")"
        cp "$from/$file" "$to/$file"
    done
    echo "synced $(find "$to" -type f -name '*.json' | wc -l | tr -d ' ') file(s) into ${to#"$MACOS_DIR/"}"
}

sync_dir "$REPO_DIR/profiles" "$MACOS_DIR/Sources/MonitorSwitcherCore/Resources/profiles"
sync_dir "$REPO_DIR/spec" "$MACOS_DIR/Tests/MonitorSwitcherCoreTests/Resources/spec"
