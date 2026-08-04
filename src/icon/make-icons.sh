#!/bin/zsh
# Regenerate the app icon (only needed when you have changed the shape of the sun)
#
#   ./make-icons.sh          light background (the default, the one that goes into the app)
#   ./make-icons.sh dark     dark background
#
# Note: the traditional macOS .icns does not switch automatically with the system's light/dark
# appearance — what can switch is the .icon format in macOS 26, and that can only be made with
# Xcode's Icon Composer (pure GUI). The asset catalogue route is a dead end:
# actool accepts the dark variants without a single error, but they are simply not there in
# the Assets.car it produces
# (the mac idiom's appiconset does not support light/dark variants; that is an iOS mechanism,
# and assetutil will confirm it).
# So this is a manual switch instead: once you have switched, just run build.sh once.
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
