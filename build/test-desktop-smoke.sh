#!/usr/bin/env bash
# Native desktop acceptance using isolated application data and synthetic media.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app="${1:?Usage: bash build/test-desktop-smoke.sh <published-desktop-binary>}"
if [[ "$app" != /* ]]; then app="$PWD/$app"; fi
if [[ ! -x "$app" ]]; then echo 'Published binary does not exist or is not executable.' >&2; exit 2; fi
command -v ffmpeg >/dev/null || { echo 'ffmpeg is required for the synthetic acceptance fixture.' >&2; exit 2; }
mkdir -p "$root/.alma"
work="$(mktemp -d "$root/.alma/desktop-acceptance.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/data"
ffmpeg -v error -y -f lavfi -i testsrc2=size=640x360:rate=25 \
  -f lavfi -i sine=frequency=440:sample_rate=48000 -t 20 \
  -c:v libx264 -pix_fmt yuv420p -c:a aac "$work/media.mp4"
VODBOX_DATA_DIR="$work/data" "$app" --ui-smoke --media="$work/media.mp4"
