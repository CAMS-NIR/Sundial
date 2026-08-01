#!/bin/zsh
# 重新生成 App 图标（改了太阳的造型才需要跑）
#
#   ./生成图标.sh          浅色底（默认，装进 App）
#   ./生成图标.sh dark     深色底
#
# 说明：macOS 的传统 .icns 不会跟随系统明暗自动切换——能切换的是 macOS 26 的
# .icon 格式，只能用 Xcode 的 Icon Composer（纯 GUI）做。资源目录那条路走不通：
# actool 会零报错地收下深色变体，但编出来的 Assets.car 里根本没有它们
# （mac idiom 的 appiconset 不支持明暗变体，那是 iOS 的机制，assetutil 可验证）。
# 所以这里做成手动切换：换完跑一次 build.sh 即可。
set -e
cd "$(dirname "$0")"
rm -rf Sundial.iconset Sundial-dark.iconset
mkdir -p Sundial.iconset Sundial-dark.iconset
swiftc -O -o .gen main.swift
./.gen
iconutil -c icns Sundial.iconset      -o ../Sundial-light.icns
iconutil -c icns Sundial-dark.iconset -o ../Sundial-dark.icns
rm -f .gen

if [[ "${1:-light}" == "dark" ]]; then
  cp ../Sundial-dark.icns ../Sundial.icns
  echo "✓ 已启用【深色】图标；跑一次 ../build.sh 生效"
else
  cp ../Sundial-light.icns ../Sundial.icns
  echo "✓ 已启用【浅色】图标；跑一次 ../build.sh 生效"
fi
echo "  两个版本都留在 ../Sundial-light.icns 和 ../Sundial-dark.icns"
