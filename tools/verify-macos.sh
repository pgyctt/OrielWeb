#!/usr/bin/env bash
# macOS 后端冒烟与取证（目标环境：GitHub 托管的 macOS runner；真 Mac 上同样可跑）。
#
# 机器可判定的断言：
#   1) 进程存活至观察窗结束——启动即崩（签名、依赖、平台守卫）会立刻暴露；
#   2) 出现 WebKit 的 WebContent 子进程——它只在 webview 真的开始加载页面时才出现
#      （与 Linux 那轮的 WebKitWebProcess 同一角色）；
#   3) CGWindowList 枚举到标题含 "Oriel Demo" 的窗口——直接证明 host.Create() 真的建出了 NSWindow。
# 只能人眼判定：截图里的页面渲染、中文、右下角徽章是否已变为「IPC 已连接」。截图作 artifact 上传。
#
# 为什么先做 ad-hoc 签名：Apple Silicon 要求可执行文件至少有 ad-hoc 签名，否则内核直接杀掉
# （Killed: 9）。dotnet 的 AOT 产物通常已带签名，这里再做一次幂等的强制补签，避免"发布成功却
# 跑不起来"这种假阴性被误读成代码缺陷。
#
# 用法：
#   bash tools/verify-macos.sh                  # 默认 20 秒观察窗，含 AOT 发布
#   bash tools/verify-macos.sh --seconds 30     # 加长观察窗
#   bash tools/verify-macos.sh --no-publish     # 跳过发布，直接跑已有产物
#   bash tools/verify-macos.sh --rid osx-x64    # 指定 RID（默认按 uname -m 推断）
#   bash tools/verify-macos.sh --out DIR        # 产物目录（默认 <仓库根>/macos-verify-out）
#
# 退出码：0 = 三项机器断言全部成立；非 0 = 有断言不成立（细节见输出与产物目录）。
#
# 写作约定（CI 上真实踩到过）：脚本里的变量一律写成 ${VAR} 形式。紧贴中文/全角字符的 $VAR
# 在非 UTF-8 locale 下（GitHub runner 就是）会被 bash 连同多字节字符一起当作变量名，
# 配合 set -u 直接 unbound variable 退出——本地 UTF-8 环境跑不出来，只在 CI 上炸。

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OBSERVE_SECONDS=20
DO_PUBLISH=1
OUT="$REPO_ROOT/macos-verify-out"
WINDOW_TITLE_PATTERN="Oriel Demo"
RID=""

usage() {
    sed -n '2,/^set -uo pipefail/p' "$0" | sed '$d' | sed 's/^#\{1,2\} \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --seconds)    OBSERVE_SECONDS="$2"; shift 2 ;;
        --rid)        RID="$2"; shift 2 ;;
        --out)        OUT="$2"; shift 2 ;;
        --no-publish) DO_PUBLISH=0; shift ;;
        -h|--help)    usage; exit 0 ;;
        *)            echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ -z "$RID" ]]; then
    case "$(uname -m)" in
        arm64)  RID="osx-arm64" ;;
        x86_64) RID="osx-x64" ;;
        *)      echo "不认识的架构 $(uname -m)，请用 --rid 指定" >&2; exit 2 ;;
    esac
fi

DEMO_EXE="$REPO_ROOT/samples/OrielDemo/bin/Release/net10.0/$RID/publish/OrielDemo"
RUN_LOG="$OUT/run.log"

mkdir -p "$OUT"
section() { echo; echo "===== $* ====="; }

section "环境"
echo "macOS     : $(sw_vers -productVersion 2>&1)（$(uname -m)）"
echo "RID       : $RID"
echo "产物      : $DEMO_EXE"
echo "产物目录  : $OUT"

if (( DO_PUBLISH )); then
    section "发布 AOT（${RID}）"
    if ! ( cd "$REPO_ROOT" && dotnet publish samples/OrielDemo/OrielDemo.csproj -c Release -r "$RID" -v minimal ); then
        echo "发布失败" >&2
        exit 1
    fi
fi

if [[ ! -x "$DEMO_EXE" ]]; then
    echo "找不到可执行产物：${DEMO_EXE}（先不加 --no-publish 跑一次）" >&2
    exit 1
fi

section "补 ad-hoc 签名（Apple Silicon 运行前提，幂等）"
if command -v codesign >/dev/null 2>&1; then
    codesign --force --sign - "$DEMO_EXE" 2>&1 | sed 's/^/  /'
    codesign -dv "$DEMO_EXE" 2>&1 | grep -m1 -E 'Signature|Identifier' | sed 's/^/  /' || true
else
    echo "  没有 codesign，跳过"
fi

section "准备窗口枚举器"
cat > "$OUT/window-list.swift" <<'SWIFT'
import CoreGraphics
import Foundation

// 列出当前屏幕上的所有窗口：owner 名称、标题、窗口号、bounds。
// 标题字段（kCGWindowName）需要「屏幕录制」权限；本仓库的验证环境已具备
// （tools/probe-macos.sh 的 C 组 screencapture 成功即为证据）。取不到标题时仍可按 owner 匹配。
let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
guard let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
    FileHandle.standardError.write(Data("无法获取窗口列表\n".utf8))
    exit(2)
}

for window in windows {
    let owner = window[kCGWindowOwnerName as String] as? String ?? "?"
    let name = window[kCGWindowName as String] as? String ?? ""
    let number = window[kCGWindowNumber as String] as? Int ?? -1

    var width = 0, height = 0, x = 0, y = 0
    if let bounds = window[kCGWindowBounds as String] as? [String: Any] {
        width = Int(bounds["Width"] as? Double ?? 0)
        height = Int(bounds["Height"] as? Double ?? 0)
        x = Int(bounds["X"] as? Double ?? 0)
        y = Int(bounds["Y"] as? Double ?? 0)
    }

    print("owner=\(owner)|name=\(name)|num=\(number)|bounds=\(width)x\(height)+\(x)+\(y)")
}
SWIFT

WINDOW_LIST="$OUT/window-list"
if command -v swiftc >/dev/null 2>&1; then
    if swiftc -O "$OUT/window-list.swift" -o "$WINDOW_LIST" 2>"$OUT/window-list.build.log"; then
        echo "  编译完成"
    else
        echo "  编译失败，窗口断言将跳过："
        sed 's/^/    /' "$OUT/window-list.build.log"
        WINDOW_LIST=""
    fi
else
    echo "  没有 swiftc，窗口断言将跳过（只断言进程与 WebKit 子进程）"
    WINDOW_LIST=""
fi

# ---------------------------------------------------------------------------
section "运行取证（观察窗 ${OBSERVE_SECONDS}s）"
echo "启动：$DEMO_EXE"
echo "请观察截图产物，确认页面右下角徽章为「IPC 已连接」（本脚本无法机器判定这一项）"
echo

"$DEMO_EXE" >"$RUN_LOG" 2>&1 &
APP_PID=$!

ALIVE=1
EXIT_CODE=""
WEBKIT_SEEN=0
WEBKIT_AT=""
WINDOW_LINE=""
WINDOW_AT=""

for ((second = 1; second <= OBSERVE_SECONDS; second++)); do
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        ALIVE=0
        # 退出码是区分"被信号杀死（原生崩溃）"与"正常退出（逻辑性退出）"的关键证据：
        # bash 对信号死亡的子进程返回 128+signo，故 >128 即为信号。
        wait "$APP_PID" 2>/dev/null
        EXIT_CODE=$?
        EXIT_HINT=""
        if (( EXIT_CODE > 128 )); then
            EXIT_HINT="，即被信号 $((EXIT_CODE - 128)) 终止"
        fi
        echo "[${second}s] 进程已退出（退出码 ${EXIT_CODE}${EXIT_HINT}）"
        break
    fi

    if (( WEBKIT_SEEN == 0 )); then
        # 用 [W] 字符类避免 pgrep -f 匹配到承载本命令的 shell 自身（命令行里含同一字符串）
        if pgrep -f "[W]ebContent" >/dev/null 2>&1; then
            WEBKIT_SEEN=1
            WEBKIT_AT="$second"
            echo "[${second}s] WebContent 子进程出现"
        fi
    fi

    if [[ -z "$WINDOW_LINE" && -n "$WINDOW_LIST" ]]; then
        "$WINDOW_LIST" >"$OUT/windows.txt" 2>/dev/null || true
        line="$(grep -F "$WINDOW_TITLE_PATTERN" "$OUT/windows.txt" | head -1)"
        if [[ -n "$line" ]]; then
            WINDOW_LINE="$line"
            WINDOW_AT="$second"
            echo "[${second}s] 发现窗口：$line"
            # 窗口刚出现就截图：页面可能还在渲染，故稍后再截一张最终图
            screencapture -x "$OUT/shot-early.png" 2>/dev/null || true
        fi
    fi

    sleep 1
done

# 收尾复检：避免"刚好在最后一秒才出现"被判否
if kill -0 "$APP_PID" 2>/dev/null; then
    if (( WEBKIT_SEEN == 0 )) && pgrep -f "[W]ebContent" >/dev/null 2>&1; then
        WEBKIT_SEEN=1
        WEBKIT_AT="≤${OBSERVE_SECONDS}"
    fi
    if [[ -z "$WINDOW_LINE" && -n "$WINDOW_LIST" ]]; then
        "$WINDOW_LIST" >"$OUT/windows.txt" 2>/dev/null || true
        WINDOW_LINE="$(grep -F "$WINDOW_TITLE_PATTERN" "$OUT/windows.txt" | head -1)"
        WINDOW_AT="≤${OBSERVE_SECONDS}"
    fi
fi

section "截图（人眼判定用）"
if screencapture -x "$OUT/shot-final.png" 2>"$OUT/screencapture.log"; then
    ls -l "$OUT/shot-final.png" | sed 's/^/  /'
    sips -g pixelWidth -g pixelHeight "$OUT/shot-final.png" 2>&1 | sed 's/^/  /'
else
    echo "  screencapture 失败："
    sed 's/^/    /' "$OUT/screencapture.log"
fi

section "崩溃报告与系统日志（进程异常退出时才有内容）"
# ReportCrash 是异步写报告的：进程刚退就查往往还没有，稍等一下
sleep 3
CRASH_DIR="$HOME/Library/Logs/DiagnosticReports"
if [[ -d "$CRASH_DIR" ]]; then
    CRASH_FOUND=0
    while IFS= read -r crash; do
        [[ -n "$crash" ]] || continue
        CRASH_FOUND=1
        cp "$crash" "$OUT/" 2>/dev/null || true
        echo "  已收集：$(basename "$crash")"
        # .ips 是 JSON：摘要出异常类型/信号/终止原因，直接进 CI 日志，省去下载 artifact
        # .ips 的 payload 是单行紧凑 JSON：先截断再打印，否则整行会把 CI 日志打爆
        grep -m4 -E '"(exception|termination|signal|faultingThread)"' "$crash" 2>/dev/null | cut -c1-400 | sed 's/^/    /' || true
    done < <(find "$CRASH_DIR" -maxdepth 1 -name '*OrielDemo*' -type f 2>/dev/null)
    (( CRASH_FOUND == 0 )) && echo "  没有与 OrielDemo 相关的崩溃报告"
else
    echo "  没有崩溃报告目录 $CRASH_DIR"
fi

if command -v log >/dev/null 2>&1; then
    ( log show --last 2m --style compact --predicate 'process == "OrielDemo"' >"$OUT/system-log.txt" 2>&1 ) &
    LOG_QUERY_PID=$!
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        kill -0 "$LOG_QUERY_PID" 2>/dev/null || break
        sleep 1
    done
    if kill -0 "$LOG_QUERY_PID" 2>/dev/null; then
        kill "$LOG_QUERY_PID" 2>/dev/null || true
        echo "  统一日志查询超时（结果已部分写入 system-log.txt）"
    else
        echo "  统一日志已写入 system-log.txt（$(wc -l <"$OUT/system-log.txt" 2>/dev/null | tr -d ' ') 行）"
        # 关键行直接进 CI 日志，省去下载 artifact：托管运行时的 FailFast/断言在 macOS 上
        # 经 os_log 上报（不写 stderr），这里往往是唯一能看到失败原因的地方
        grep -iE 'fatal|unhandled|abort|assert|trap|exception|terminat|orieldemo' "$OUT/system-log.txt" 2>/dev/null \
            | tail -30 | cut -c1-300 | sed 's/^/    /' || true
    fi
fi

section "清理"
if kill -0 "$APP_PID" 2>/dev/null; then
    kill "$APP_PID" 2>/dev/null || true
    for _ in 1 2 3 4 5; do
        kill -0 "$APP_PID" 2>/dev/null || break
        sleep 0.4
    done
    if kill -0 "$APP_PID" 2>/dev/null; then
        echo "SIGTERM 未生效，改用 SIGKILL"
        kill -9 "$APP_PID" 2>/dev/null || true
    else
        echo "已用 SIGTERM 正常退出"
    fi
fi
wait "$APP_PID" 2>/dev/null || true

section "结果汇总"
echo "进程存活至观察窗结束 : $([[ $ALIVE -eq 1 ]] && echo 是 || echo 否)"
if [[ -n "$EXIT_CODE" ]]; then
    echo "进程退出码           : ${EXIT_CODE}"
fi
if (( WEBKIT_SEEN == 1 )); then
    echo "WebContent 子进程    : 是（第 ${WEBKIT_AT}s 起）"
else
    echo "WebContent 子进程    : 否（webview 没有真正开始加载页面）"
fi
if [[ -n "$WINDOW_LINE" ]]; then
    echo "窗口断言（标题含 \"$WINDOW_TITLE_PATTERN\"）: 是（第 ${WINDOW_AT}s 起）"
    echo "  $WINDOW_LINE"
else
    echo "窗口断言（标题含 \"$WINDOW_TITLE_PATTERN\"）: 否"
    if [[ -n "$WINDOW_LIST" ]]; then
        echo "  枚举到的其它窗口（前 10 行）："
        head -10 "$OUT/windows.txt" 2>/dev/null | sed 's/^/    /'
    fi
fi

if [[ -s "$RUN_LOG" ]]; then
    echo
    echo "进程输出（前 40 行）："
    head -40 "$RUN_LOG" | sed 's/^/  /'
else
    echo
    echo "进程输出：空（预期如此，库不往 stdout 打日志）"
fi

exit_code=0
[[ $ALIVE -eq 1 ]] || exit_code=1
[[ $WEBKIT_SEEN -eq 1 ]] || exit_code=1
[[ -n "$WINDOW_LINE" ]] || exit_code=1

echo
if [[ $exit_code -eq 0 ]]; then
    echo "机器判定：PASS（进程存活 + WebContent 子进程 + 窗口存在）"
    echo "待人工确认：截图 $OUT/shot-final.png 里的页面渲染、中文与徽章是否为「IPC 已连接」"
else
    echo "机器判定：FAIL"
    [[ $ALIVE -eq 0 ]] || echo "  - 进程在观察窗结束前就退出了，看上面的进程输出"
    [[ $WEBKIT_SEEN -eq 0 ]] && echo "  - 全程没有出现 WebContent 子进程（webview 没有真正开始加载页面）"
    [[ -z "$WINDOW_LINE" ]] && echo "  - 未枚举到标题含 \"$WINDOW_TITLE_PATTERN\" 的窗口"
fi

echo
echo "产物目录：$OUT"
exit $exit_code
