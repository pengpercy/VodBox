#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:?Usage: bash build/publish-aot.sh <rid> [output-directory]}"
output="${2:-$root/artifacts/publish/$rid}"
project="$root/src/VodBox.Desktop/VodBox.Desktop.csproj"

# Some macOS installations expose a launcher in PATH without a usable SDK.
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
  dotnet_cmd="$HOME/.dotnet/dotnet"
  export DOTNET_ROOT="$HOME/.dotnet"
else
  dotnet_cmd="$(command -v dotnet)"
fi

# Restore must use the same configuration and RID as --no-restore publish.
"$dotnet_cmd" restore "$project" -r "$rid" -p:Configuration=Release
"$dotnet_cmd" publish "$project" -c Release -r "$rid" --self-contained --no-restore -o "$output"

