#!/bin/zsh
# 编译并打包 Sundial.app
#
#   ./build.sh              本机调试用（仅 arm64，ad-hoc 签名，装到桌面）
#   ./build.sh release      发布用（通用二进制 + Developer ID 签名 + 公证）
#
# 发布前需先存好公证凭据（只需一次，密码由你自己输入，不经过脚本）：
#   xcrun notarytool store-credentials solaris-notary \
#       --apple-id <你的AppleID> --team-id <TeamID> --password <App专用密码>

set -e
cd "$(dirname "$0")"

APP_NAME="Sundial"
BUNDLE_ID="com.cams-nir.sundial"
MIN_MACOS="13.0"
NOTARY_PROFILE="solaris-notary"
MODE="${1:-debug}"
[[ "$MODE" == "share" ]] && UNIVERSAL=1 || UNIVERSAL=0

# main.swift 必须放最后（Swift 要求入口文件的顶层语句最后编译）
SRC=(${(f)"$(ls *.swift | grep -v '^main.swift$')"} main.swift)

# 编译与签名一律在临时目录里做，**不能在桌面上做**。
# 桌面开着 iCloud 同步（xattr 里能看到 com.apple.fileprovider.fpfs#P），
# 文件提供程序会异步给 .app 重新打上 com.apple.FinderInfo，和 codesign 抢时间——
# 于是 codesign 间歇性报「resource fork, Finder information ... not allowed」，
# 清多少次 xattr 都没用，因为它是在清完之后才被加回去的。
BUILD="${TMPDIR:-/tmp}/sundial-build"
rm -rf "$BUILD" && mkdir -p "$BUILD"

if [[ "$MODE" == "release" || "$MODE" == "share" ]]; then
  echo "编译通用二进制（arm64 + x86_64，最低 macOS $MIN_MACOS）..."
  swiftc -O -swift-version 5 -target arm64-apple-macos$MIN_MACOS \
         -o "$BUILD/$APP_NAME-arm64" "${SRC[@]}"
  swiftc -O -swift-version 5 -target x86_64-apple-macos$MIN_MACOS \
         -o "$BUILD/$APP_NAME-x86_64" "${SRC[@]}"
  lipo -create -output "$BUILD/$APP_NAME" \
       "$BUILD/$APP_NAME-arm64" "$BUILD/$APP_NAME-x86_64"
  DEST=""
else
  echo "编译中（${#SRC[@]} 个源文件，仅本机架构）..."
  swiftc -O -swift-version 5 -target arm64-apple-macos$MIN_MACOS \
         -o "$BUILD/$APP_NAME" "${SRC[@]}"
  DEST="$HOME/Desktop/Sundial/$APP_NAME.app"
fi
APP="$BUILD/$APP_NAME.app"      # 组装与签名都在这里，签完再搬到桌面

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Info.plist "$APP/Contents/Info.plist"
cp "$BUILD/$APP_NAME" "$APP/Contents/MacOS/$APP_NAME"
# 图标（由 图标生成器.swift 生成，改了太阳要重跑一次那个脚本）
cp $APP_NAME.icns "$APP/Contents/Resources/$APP_NAME.icns"
# 清掉扩展属性：cp 会把源文件的 xattr 一起带过来（.icns 常带着 Finder 信息）。
# 注意 com.apple.provenance 清不掉，但 codesign 不介意它，介意的是 FinderInfo
clean_xattr() { xattr -cr "$1" 2>/dev/null || true; }
clean_xattr "$APP"

if [[ "$MODE" == "release" ]]; then
  ID=$(security find-identity -v -p codesigning | grep "Developer ID Application" | head -1 \
       | sed -E 's/.*"(.*)"/\1/')
  if [[ -z "$ID" ]]; then
    echo "✗ 钥匙串里没有 Developer ID Application 证书。"
    echo "  请先在 Xcode › Settings › Accounts 里下载，或到 developer.apple.com 创建。"
    exit 1
  fi
  echo "签名：$ID"
  # runtime = 强化运行时，公证的硬性要求
  clean_xattr "$APP"
  codesign --force --options runtime --timestamp \
           --identifier "$BUNDLE_ID" --sign "$ID" "$APP"

  ZIP="$BUILD/$APP_NAME.zip"
  ditto -c -k --keepParent "$APP" "$ZIP"
  echo "提交公证（几十秒到几分钟）..."
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"         # 把公证票据钉进 App，离线也能验证

  rm -f "$ZIP"
  OUT="$HOME/Desktop/Sundial/发给朋友/$APP_NAME.zip"
  ditto -c -k --keepParent "$APP" "$OUT"
  echo "✓ 可分发文件：$OUT"
  spctl -a -vv "$APP" 2>&1 | tail -2
elif [[ "$MODE" == "debug" ]]; then
  clean_xattr "$APP"
  codesign --force --sign - "$APP"
  # 签完再搬到桌面：iCloud 之后怎么打标记都不影响已经嵌进去的签名
  rm -rf "$DEST" && ditto "$APP" "$DEST"
  echo "完成：$DEST"
fi

# share 模式：通用二进制 + ad-hoc 签名 + 打包，给没有开发者帐号时用
if [[ "$MODE" == "share" ]]; then
  clean_xattr "$APP"
  codesign --force --sign - "$APP"
  OUT="$HOME/Desktop/Sundial/发给朋友/$APP_NAME.zip"
  rm -f "$OUT"
  ditto -c -k --keepParent "$APP" "$OUT"
  echo "✓ 可分发文件：$OUT（朋友首次打开需去隔离，见 给朋友看的说明.txt）"
fi
