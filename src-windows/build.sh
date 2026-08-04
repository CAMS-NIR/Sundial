#!/bin/zsh
# Cross-package the Windows build of Sundial on a Mac
#
#   ./build.sh            package win-x64 (Intel/AMD 64-bit, the vast majority of Windows machines)
#   ./build.sh arm64      package win-arm64 (Snapdragon X / Surface Pro 11 and the like)
#   ./build.sh check      run the checks only: Core logic + off-screen render to a PNG
#
# Requires the .NET SDK: brew install dotnet
set -e
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || export PATH="/opt/homebrew/bin:$PATH"

MODE="${1:-x64}"
# The single source of truth for the version number is the VERSION file at the repo root
# (shared with the macOS build).
# Info.plist and the csproj each used to carry their own copy, and changing one and
# forgetting the other was only a matter of time
VERSION="$(cat ../VERSION 2>/dev/null || echo 0.0.0)"

if [[ "$MODE" == "check" ]]; then
  echo "▸ 拿本机真实的 Claude Code 记录跑 Core 层…"
  dotnet run --project tests/LiveCheck/LiveCheck.csproj
  echo "\n▸ 离屏渲染界面到 /tmp/sundial-win-*.png…"
  dotnet run --project tests/RenderCheck/RenderCheck.csproj -- /tmp
  echo "✓ 验证完成"
  exit 0
fi

case "$MODE" in
  arm64) RID="win-arm64"; OUT="dist-Windows-arm64" ;;
  x64)   RID="win-x64";   OUT="dist-Windows" ;;
  *) echo "不认识的参数：$MODE（可用 x64 / arm64 / check）"; exit 1 ;;
esac

echo "▸ 打包 $RID（自包含，对方不用装 .NET 运行时）…"
rm -rf "$OUT"
dotnet publish src/Sundial.App/Sundial.App.csproj \
  -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "$OUT" | tail -2

rm -f "$OUT"/*.pdb                      # no need to ship the debug symbols
cp INSTALL.txt "$OUT/" 2>/dev/null || true

echo "✓ 产物：$PWD/$OUT/Sundial.exe（$(du -h "$OUT/Sundial.exe" | cut -f1)）"
file "$OUT/Sundial.exe"
