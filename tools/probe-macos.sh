#!/usr/bin/env bash
# macOS 能力探针：回答「这台机器能不能运行本机 AppKit/WKWebView 窗口应用」。
# 主要目标环境是 GitHub 托管的 macOS runner，在真 Mac 上同样可跑。
#
# 为什么需要这一步：托管 macOS runner 是否具备 GUI（Aqua）会话，官方文档没有明文。
# 「能跑 iOS 模拟器上的 XCUITest」不能作为证据——模拟器有自己的渲染路径，不需要本机窗口。
# 所以这里用最小程序直接探：能不能建出可见的 NSWindow、能不能真截到像素、WKWebView 能不能加载页面。
#
# 探针分五组：
#   A. 系统与会话：sw_vers / launchctl managername（Aqua 才是有 GUI 的会话）/ /dev/console 属主 / autologin 配置
#   B. AppKit 建窗：最小 Swift 程序创建 NSWindow，打印 NSScreen 数量、isVisible、windowNumber
#   C. 截图能力：screencapture 能否截到像素（尺寸与字节数），失败时记录原因（多为屏幕录制权限）
#   D. WebKit 能力：最小 WKWebView 程序加载 HTML 并等 didFinish，同时列出 WebKit 子进程
#   E. 中文字体：系统是否自带 PingFang（macOS 自带，用于与 Linux 那轮缺字体对照）
#
# 产物写到 --out 指定目录（默认 ./macos-probe-out）：截图、探针源码与编译/运行日志。
#
# 用法：
#   bash tools/probe-macos.sh
#   bash tools/probe-macos.sh --out /tmp/macos-probe-out
#
# 退出码：0 表示探针跑完（**不代表**结论为「可用」——结论看输出与产物）；非 0 只出现在脚本自身错误。

set -uo pipefail

OUT="$(pwd)/macos-probe-out"

usage() {
    sed -n '2,/^set -uo pipefail/p' "$0" | sed '$d' | sed 's/^#\{1,2\} \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out)      OUT="$2"; shift 2 ;;
        -h|--help)  usage; exit 0 ;;
        *)          echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$OUT"
step() { echo; echo "===== $* ====="; }

# ---------------------------------------------------------------------------
step "A. 系统与会话"

echo "sw_vers:"; sw_vers 2>&1 | sed 's/^/  /'
echo "arch          : $(uname -m)"
echo "whoami        : $(whoami)"
echo "id -u         : $(id -u)"
echo "/dev/console  : $(stat -f '%Su' /dev/console 2>&1)（谁拥有控制台；runner 用户名说明有图形登录会话）"
echo "managername   : $(launchctl managername 2>&1)（Aqua = 图形会话；Background/System = 无 GUI）"
echo "who:"; who 2>&1 | sed 's/^/  /'
echo "autologin 用户: $(defaults read /Library/Preferences/com.apple.loginwindow autoLoginUser 2>&1 | head -1)"
echo "关键进程:"; ps -axo comm 2>/dev/null | grep -E 'WindowServer|loginwindow|Dock' | sort -u | sed 's/^/  /'
echo "xcode-select  : $(xcode-select -p 2>&1)"

# ---------------------------------------------------------------------------
step "B. AppKit 建窗探针"

cat > "$OUT/probe-appkit.swift" <<'SWIFT'
import AppKit

// 最小探针：当前会话里能否创建并显示一个 NSWindow。
// 无 GUI 会话时，NSScreen.screens 为空数组，窗口也无法 makeKeyAndOrderFront。
let app = NSApplication.shared
app.setActivationPolicy(.regular)

print("NSScreen.screens.count=\(NSScreen.screens.count)")
if let main = NSScreen.main {
    print("NSScreen.main.frame=\(main.frame)")
} else {
    print("NSScreen.main=nil")
}

let window = NSWindow(
    contentRect: NSRect(x: 100, y: 100, width: 320, height: 200),
    styleMask: [.titled, .closable, .resizable],
    backing: .buffered,
    defer: false)
window.title = "Oriel Probe"
window.makeKeyAndOrderFront(nil)

print("window.isVisible=\(window.isVisible)")
print("window.windowNumber=\(window.windowNumber)")

DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
    print("after 2s isVisible=\(window.isVisible) windowNumber=\(window.windowNumber)")
    app.terminate(nil)
}
app.run()
SWIFT

if command -v swiftc >/dev/null 2>&1; then
    if swiftc -O "$OUT/probe-appkit.swift" -o "$OUT/probe-appkit" 2>"$OUT/probe-appkit.build.log"; then
        "$OUT/probe-appkit" 2>&1 | tee "$OUT/probe-appkit.log"
        echo "退出码：${PIPESTATUS[0]}"
    else
        echo "swiftc 编译失败："
        sed 's/^/  /' "$OUT/probe-appkit.build.log"
    fi
else
    echo "无 swiftc（未安装 Xcode 或命令行工具）"
fi

# ---------------------------------------------------------------------------
step "C. 截图能力"

if screencapture -x "$OUT/shot.png" 2>"$OUT/screencapture.log"; then
    echo "screencapture 成功"
    ls -l "$OUT/shot.png" | sed 's/^/  /'
    sips -g pixelWidth -g pixelHeight "$OUT/shot.png" 2>&1 | sed 's/^/  /'
else
    code=$?
    echo "screencapture 失败（退出码 $code）："
    sed 's/^/  /' "$OUT/screencapture.log"
fi

# ---------------------------------------------------------------------------
step "D. WKWebView 探针"

cat > "$OUT/probe-webview.swift" <<'SWIFT'
import AppKit
import WebKit

// 列出与 WebKit 相关的进程：承载页面渲染的 WebContent 只在真的开始加载页面后才出现。
func dumpWebKitProcesses(_ tag: String) {
    let task = Process()
    task.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
    task.arguments = ["-fl", "WebContent|Networking|WebKit"]
    let pipe = Pipe()
    task.standardOutput = pipe
    task.standardError = pipe
    do {
        try task.run()
        task.waitUntilExit()
    } catch {
        print("[\(tag)] pgrep 调用失败：\(error)")
        return
    }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    let text = String(data: data, encoding: .utf8) ?? ""
    print("[\(tag)] WebKit 相关进程：")
    print(text.isEmpty ? "  （无）" : text)
}

final class NavDelegate: NSObject, WKNavigationDelegate {
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        print("didFinish url=\(webView.url?.absoluteString ?? "nil")")
        dumpWebKitProcesses("didFinish")
        webView.evaluateJavaScript("document.title") { value, error in
            if let error = error {
                print("evaluateJavaScript 错误：\(error)")
            }
            print("document.title=\(value ?? "nil")")
            NSApplication.shared.terminate(nil)
        }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        print("didFail 错误：\(error)")
        NSApplication.shared.terminate(nil)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        print("didFailProvisional 错误：\(error)")
        NSApplication.shared.terminate(nil)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)

let window = NSWindow(
    contentRect: NSRect(x: 0, y: 0, width: 480, height: 320),
    styleMask: [.titled, .closable, .resizable],
    backing: .buffered,
    defer: false)
window.title = "Oriel WKWebView Probe"

let webView = WKWebView(
    frame: NSRect(x: 0, y: 0, width: 480, height: 320),
    configuration: WKWebViewConfiguration())
let delegate = NavDelegate()
webView.navigationDelegate = delegate
window.contentView = webView
window.makeKeyAndOrderFront(nil)

print("发起 loadHTMLString；window.isVisible=\(window.isVisible) windowNumber=\(window.windowNumber)")
webView.loadHTMLString(
    "<html><head><title>oriel-probe</title></head><body><p>中文探针 ok</p></body></html>",
    baseURL: nil)

// 兜底：15 秒没等到回调也要收尾，避免探针把 CI 卡到超时上限
DispatchQueue.main.asyncAfter(deadline: .now() + 15) {
    print("兜底超时：15 秒内未收到导航回调")
    dumpWebKitProcesses("timeout")
    NSApplication.shared.terminate(nil)
}
app.run()
SWIFT

if command -v swiftc >/dev/null 2>&1; then
    if swiftc -O "$OUT/probe-webview.swift" -o "$OUT/probe-webview" 2>"$OUT/probe-webview.build.log"; then
        "$OUT/probe-webview" 2>&1 | tee "$OUT/probe-webview.log"
        echo "退出码：${PIPESTATUS[0]}"
    else
        echo "swiftc 编译失败："
        sed 's/^/  /' "$OUT/probe-webview.build.log"
    fi
else
    echo "无 swiftc（未安装 Xcode 或命令行工具）"
fi

# ---------------------------------------------------------------------------
step "E. 中文字体"

for font in \
    /System/Library/Fonts/PingFang.ttc \
    "/System/Library/Fonts/STHeiti Light.ttc" \
    /System/Library/Fonts/Hiragino\ Sans\ GB.ttc
do
    if [[ -e "$font" ]]; then
        echo "存在：$font"
    else
        echo "缺失：$font"
    fi
done

# ---------------------------------------------------------------------------
step "结论读法（本脚本只取证，不做判定）"

cat <<'README'
- A：managername = Aqua（且 /dev/console 属主是当前用户、WindowServer 在跑）→ 有图形会话
- B：NSScreen.screens.count > 0 且 window.isVisible=true、windowNumber > 0 → **确实建出了窗口**
- C：screencapture 成功且像素尺寸非零 → 能真的截到像素（失败通常是缺「屏幕录制」TCC 授权）
- D：didFinish 且 document.title=oriel-probe、且列出的 WebKit 进程里有 WebContent
     → **WKWebView 在本机可用**，即本库 macOS 后端的技术路线在该环境可行
- E：PingFang 存在 → 中文不会像 Linux 那轮那样渲染成方框

若 B 或 D 不成立（NSScreen 为空 / 建窗失败 / didFinish 未收到），则托管 runner 不适合做
窗口级验证，需要改走云端 Mac 或实体 Mac（见 docs/DECISIONS.md 的 macOS 验证记录）。
README

echo
echo "产物目录：$OUT"
exit 0
