#!/bin/zsh
# 在 Mac 上交叉打包 Windows 版 Sundial
#
#   ./build.sh            打包 win-x64（Intel/AMD 64 位，绝大多数 Windows 机器）
#   ./build.sh arm64      打包 win-arm64（骁龙 X / Surface Pro 11 这类）
#   ./build.sh check      只跑验证：Core 逻辑 + 离屏渲染出 PNG
#
# 需要 .NET SDK：brew install dotnet
set -e
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || export PATH="/opt/homebrew/bin:$PATH"

MODE="${1:-x64}"
# 版本号的唯一来源是仓库根的 VERSION 文件（与 macOS 版共用）。
# 以前 Info.plist 和 csproj 各写各的，改一边忘另一边是迟早的事
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

rm -f "$OUT"/*.pdb                      # 调试符号不用发出去
cp INSTALL.txt "$OUT/" 2>/dev/null || true

echo "✓ 产物：$PWD/$OUT/Sundial.exe（$(du -h "$OUT/Sundial.exe" | cut -f1)）"
file "$OUT/Sundial.exe"
