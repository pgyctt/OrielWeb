#!/usr/bin/env bash
# 把 macOS 的 AOT 产物包成一个可分发的 .app。
#
# 为什么非包不可：WKWebView 在 macOS 上是多进程架构，宿主进程要凭 main bundle 的身份
# （Info.plist 的 CFBundleIdentifier）才能与 WebContent / Networking 这些 XPC 服务通信。
# 裸可执行文件没有 bundle identifier 时，AppKit 会打出
# "Cannot index window tabs due to missing main bundle identifier"，随后 WebKit 在内部断言处
# __builtin_trap() → SIGTRAP（退出码 133），而且不产生崩溃报告——现象是"进程不崩消息循环、
# 直接死掉"。真机上就是这么炸的。
#
# Apple Silicon 还要求可执行代码有签名（ad-hoc 即可），所以两个层次都签：先签可执行文件，
# 再签整个 bundle。签名只是让本机能跑，不代表可以被别人直接打开——没有 Developer ID 与
# 公证的包从网上下载后会被 Gatekeeper 拦下，需要去掉 quarantine 属性（见 README）。
#
# 用法：
#   tools/make-macos-app.sh <可执行文件> <输出目录> [版本号]
#
# 产物：<输出目录>/OrielDemo.app
#
# 与 tools/verify-macos.sh 的关系：那个脚本里有一份等价的内联实现（连解释都相同），它服务于
# 运行验证，版本号写死；本脚本服务于分发，版本号由调用方给。将来验证脚本可以切到它上面。
set -euo pipefail

EXE=${1:?用法: make-macos-app.sh <可执行文件> <输出目录> [版本号]}
OUT=${2:?用法: make-macos-app.sh <可执行文件> <输出目录> [版本号]}
VERSION=${3:-0.0.0}

NAME=OrielDemo
APP="$OUT/$NAME.app"

[[ -f "$EXE" ]] || { echo "找不到要打包的可执行文件：$EXE" >&2; exit 1; }

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$EXE" "$APP/Contents/MacOS/$NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>$NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.orielweb.demo</string>
    <key>CFBundleName</key>
    <string>$NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
</dict>
</plist>
PLIST

# 先签可执行文件（Apple Silicon 的运行前提），再签 bundle：
# bundle 的签名要覆盖 Contents/MacOS 里的那份，顺序反了会被内层未签名的代码破坏。
codesign --force --sign - "$APP/Contents/MacOS/$NAME"
codesign --force --deep --sign - "$APP"
codesign -dv "$APP" 2>&1 | grep -m1 -E 'Signature|Identifier' || true

echo "已生成 $APP（版本 $VERSION）"
