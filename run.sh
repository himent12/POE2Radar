#!/usr/bin/env bash
# Build and launch POE2Radar on Arch / CachyOS / any Linux with the .NET 10 SDK.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Need the .NET 10 SDK."
  echo "  Arch/CachyOS:  sudo pacman -S dotnet-sdk-10.0"
  echo "  otherwise:     curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0"
  exit 1
fi

scope="$(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null || echo unknown)"
if [[ "$scope" != "0" ]]; then
  echo "PoE2 (Proton) memory reads need ptrace. yama.ptrace_scope=$scope (Arch default is 1)."
  echo "Lowering it for this boot (sudo password once) — nothing is persisted."
  sudo sysctl -w kernel.yama.ptrace_scope=0
  echo "To keep this after reboot:"
  echo "  echo 'kernel.yama.ptrace_scope = 0' | sudo tee /etc/sysctl.d/10-ptrace.conf"
  echo
fi

dotnet build "$ROOT/POE2Radar.slnx" -c Release
echo
echo "Start Path of Exile 2 (Proton), load into a zone, then this overlay."
echo "Use borderless windowed (not exclusive fullscreen). F9 quits. Dashboard: http://localhost:7777"
echo
exec dotnet exec "$ROOT/src/POE2Radar.Overlay/bin/Release/net10.0/POE2Radar.Overlay.dll" "$@"
