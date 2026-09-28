#!/usr/bin/env bash
#
# verify-linux.sh —— 验证 OrielWeb 的 Linux 后端能否「真的跑起来」。
#
# 与 CI 的分工：.github/workflows/ci.yml 的 smoke-linux 在 xvfb 下只断言「进程存活到
# timeout（退出码 124）」，无法证明窗口真的创建、内嵌资源真的加载、IPC 真的往返。
# 本脚本在真实显示环境（WSL2 + WSLg、物理机桌面，或 xvfb-run 包裹均可）下运行
# AOT 发布产物，并用 xwininfo 断言窗口存在，把原始输出留档。
#
# 取证边界（重要）：
#   - 机器可判定：
#       1) 进程是否存活到观察窗结束；
#       2) 是否拉起 WebKit 子进程（WebKitWebProcess / WebKitNetworkProcess）——它只在
#          webview 真的开始加载页面时才出现，是「窗口已创建且 webview 在工作」的宿主侧
#          硬证据，且与后端是 Wayland 还是 X11 无关；
#       3) 强制 GDK_BACKEND=x11 时，是否存在标题含 "Oriel Demo" 的 X 窗口。
#   - 只能人眼判定：页面右下角徽章是否从「IPC 连接中…」变为「IPC 已连接」。该徽章由
#     wwwroot/app.js 在启动时自动发起一次 todo.list 往返后改写；本仓库没有把页面
#     console 转发到宿主 stdout，demo 也没有任何 stdout 探针或退出码语义，因此 IPC
#     往返成功与否**没有**宿主侧可观测信号，必须由人看着窗口确认。
#
# 为什么要跑两条路径：WSLg 下 GTK 默认走 Wayland，窗口注册在 Weston 合成器而非
# XWayland，xwininfo 枚举不到——「窗口是否存在」无法用 X 工具机器判定。因此默认后端那次
# 只断言「存活 + WebKit 子进程」，再补一次 GDK_BACKEND=x11 让窗口落到 XWayland，用
# xwininfo 做窗口断言。两条路径观察的是同一份页面，人眼结论可互相印证。
#
# 用法：
#   ./tools/verify-linux.sh                  # 默认 30 秒观察窗，双路径（auto）
#   ./tools/verify-linux.sh --seconds 60     # 加长观察窗
#   ./tools/verify-linux.sh --rid linux-arm64
#   ./tools/verify-linux.sh --no-publish     # 跳过发布，直接跑已有产物
#   ./tools/verify-linux.sh --backend x11    # 只跑强制 X11 那条路径
#   ./tools/verify-linux.sh --backend wayland # 只跑默认后端那条路径
#
# 系统依赖（需要 sudo，本脚本只检测并提示，不自动安装）：
#   sudo apt-get install -y libwebkit2gtk-4.1-dev libgtk-3-dev x11-utils clang zlib1g-dev
#     libwebkit2gtk-4.1-dev  运行期与编译期的 WebKitGTK 4.1
#     libgtk-3-dev           GTK3 头文件与库（构建 System.Globalization 无关，属互操作需要）
#     x11-utils              xwininfo，用于枚举 X 窗口做客观取证
#     clang / zlib1g-dev     Native AOT 的原生工具链前置
#
# .NET 10 SDK：本库只提供 net10.0 目标，Ubuntu 官方 apt 源没有该版本，用官方脚本装到用户目录：
#   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
# 装好后脚本会自动发现 $HOME/.dotnet/dotnet，也可以显式指定：DOTNET=/path/to/dotnet ./tools/verify-linux.sh
#
set -euo pipefail

RID="${RUNTIME_ID:-linux-x64}"
OBSERVE_SECONDS=30
DO_PUBLISH=1
BACKEND="auto"   # auto | wayland | x11
WINDOW_TITLE_PATTERN="Oriel Demo"

usage() {
    # 打印开头的注释块：从第 2 行到 set -euo pipefail 之前，去掉注释符
    sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^#\{1,2\} \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --seconds)   OBSERVE_SECONDS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        --backend)   BACKEND="$2"; shift 2 ;;
        --no-publish) DO_PUBLISH=0; shift ;;
        -h|--help)   usage; exit 0 ;;
        *)           echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$BACKEND" in
    auto|wayland|x11) ;;
    *) echo "未知 --backend：$BACKEND（可选 auto / wayland / x11）" >&2; exit 2 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEMO_PROJECT="$REPO_ROOT/samples/OrielDemo/OrielDemo.csproj"
PUBLISH_DIR="$REPO_ROOT/samples/OrielDemo/bin/Release/net10.0/$RID/publish"
DEMO_EXE="$PUBLISH_DIR/OrielDemo"

section() { printf '\n=== %s ===\n' "$1"; }

section "环境检查"

missing=()

# 系统依赖：dpkg 存在才用 dpkg 查（非 Debian 系发行版跳过包名检查）
if command -v dpkg >/dev/null 2>&1; then
    dpkg -s libwebkit2gtk-4.1-dev >/dev/null 2>&1 || missing+=("libwebkit2gtk-4.1-dev")
    dpkg -s libgtk-3-dev            >/dev/null 2>&1 || missing+=("libgtk-3-dev")
    dpkg -s zlib1g-dev              >/dev/null 2>&1 || missing+=("zlib1g-dev")
fi
command -v xwininfo >/dev/null 2>&1 || missing+=("x11-utils")
command -v clang    >/dev/null 2>&1 || missing+=("clang")

# .NET 10 SDK：优先用 DOTNET 环境变量，其次 PATH，最后用户目录安装
DOTNET_BIN="${DOTNET:-}"
if [[ -z "$DOTNET_BIN" ]]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET_BIN="dotnet"
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET_BIN="$HOME/.dotnet/dotnet"
    fi
fi

echo "发行版      : $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" || echo 未知)"
echo "内核        : $(uname -r)"
echo "架构 / RID  : $(uname -m) / $RID"
echo "DISPLAY     : ${DISPLAY:-<空>}（WSLg 通常为 :0；WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-<空>}）"
echo "dotnet      : ${DOTNET_BIN:-<未找到>}"

if [[ -n "$DOTNET_BIN" ]]; then
    echo "dotnet 版本 : $("$DOTNET_BIN" --version)"
fi

if [[ ${#missing[@]} -gt 0 ]]; then
    section "缺少系统依赖，验证无法继续"
    printf '  - %s\n' "${missing[@]}"
    echo
    echo "请先执行（需要 sudo，只装一次）："
    echo "  sudo apt-get install -y ${missing[*]}"
    echo
    echo "注意：x11-utils 只用于窗口取证，clang/zlib1g-dev 只在 AOT 发布时需要。"
    exit 2
fi

if [[ -z "$DOTNET_BIN" ]]; then
    section "缺少 .NET 10 SDK"
    echo "安装到用户目录（无需 sudo）："
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
    exit 2
fi

if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
    section "没有显示环境"
    echo "DISPLAY 与 WAYLAND_DISPLAY 都为空，窗口无法创建。"
    echo "WSL 里请确认 WSLg 可用（ls /mnt/wslg）；无头环境请用 xvfb-run 包裹本脚本。"
    exit 2
fi

if [[ ! -f "$DEMO_PROJECT" ]]; then
    echo "找不到示例项目：$DEMO_PROJECT" >&2
    exit 2
fi

if [[ "$DO_PUBLISH" -eq 1 ]]; then
    section "AOT 发布（$RID）"
    echo "命令：$DOTNET_BIN publish samples/OrielDemo -c Release -r $RID"
    # /mnt 下的 Windows 工作区跨文件系统，首次发布可能较慢
    "$DOTNET_BIN" publish "$DEMO_PROJECT" -c Release -r "$RID" -v minimal
fi

if [[ ! -x "$DEMO_EXE" ]]; then
    section "找不到发布产物"
    echo "期望路径：$DEMO_EXE"
    echo "请先不带 --no-publish 运行本脚本，或确认 RID 是否正确。"
    exit 2
fi

section "双路径取证（每条路径观察窗 ${OBSERVE_SECONDS}s）"
echo "产物：$DEMO_EXE"
echo
echo "请观察弹出的窗口，并确认页面右下角徽章显示「IPC 已连接」"
echo "（该徽章 = 内嵌资源加载成功 + todo.list 一次 IPC 往返成功；本脚本无法机器判定这一项）"
echo

# 基线：脚本启动前已存在的 WebKit 进程。之后用差集判定「本次运行拉起的 WebKit 子进程」，
# 避免把别处残留的 WebKit 进程算作本次证据。
BASELINE_WEBKIT="$(pgrep -f 'WebKitWebProcess|WebKitNetworkProcess' 2>/dev/null | sort -n | tr '\n' ' ' || true)"

new_webkit_pids() {
    local p out=""
    for p in $(pgrep -f 'WebKitWebProcess|WebKitNetworkProcess' 2>/dev/null | sort -n || true); do
        case " $BASELINE_WEBKIT " in
            *" $p "*) ;;
            *) out="$out$p " ;;
        esac
    done
    printf '%s' "$out"
}

CASE_LABEL=(); CASE_BACKEND=(); CASE_ALIVE=(); CASE_WEBKIT=(); CASE_WEBKIT_NOTE=()
CASE_WINDOW=(); CASE_WINDOW_AT=(); CASE_EVIDENCE=(); CASE_LOG=()

run_case() {
    local label="$1" backend="$2"
    local log pid second
    log="$(mktemp -t oriel-case-XXXXXX.log)"

    echo "--- $label ---"
    if [[ "$backend" == "x11" ]]; then
        echo "启动：GDK_BACKEND=x11 $DEMO_EXE"
        GDK_BACKEND=x11 "$DEMO_EXE" >"$log" 2>&1 &
    else
        echo "启动：$DEMO_EXE（后端交给 GDK 自行选择，WSLg 下即 Wayland）"
        "$DEMO_EXE" >"$log" 2>&1 &
    fi
    pid=$!

    local alive=1 webkit=0 webkit_note="" seen=0 first_at="" evidence=""
    for ((second = 1; second <= OBSERVE_SECONDS; second++)); do
        if ! kill -0 "$pid" 2>/dev/null; then
            alive=0
            echo "  [${second}s] 进程已退出（提前退出）"
            break
        fi

        if [[ $webkit -eq 0 ]]; then
            local wp
            wp="$(new_webkit_pids)"
            if [[ -n "$wp" ]]; then
                webkit=1
                webkit_note="第 ${second}s 拉起 pid $(echo "$wp" | tr ' ' ',' | sed 's/,$//')"
                echo "  [${second}s] WebKit 子进程出现（$webkit_note）"
            fi
        fi

        # 只有 X11 后端才可能被 xwininfo 枚举到（Wayland 窗口在 Weston 里，X 树里没有）
        if [[ "$backend" == "x11" && $seen -eq 0 ]]; then
            # 末尾的 || true 是必需的：pipefail 下 grep 无匹配会让整条管道返回非零，
            # 进而让这个赋值语句触发 set -e 把脚本静默终止。
            evidence="$(xwininfo -root -tree 2>/dev/null | grep -F "$WINDOW_TITLE_PATTERN" | head -3 || true)"
            if [[ -n "$evidence" ]]; then
                seen=1
                first_at="$second"
                echo "  [${second}s] 发现窗口：$(echo "$evidence" | head -1 | sed 's/^ *//')"
            fi
        fi

        sleep 1
    done

    # 收尾复检：避免「刚好在最后一秒才出现」被判否
    if [[ $webkit -eq 0 ]]; then
        local wp_end
        wp_end="$(new_webkit_pids)"
        if [[ -n "$wp_end" ]]; then
            webkit=1
            webkit_note="观察窗末尾拉起 pid $(echo "$wp_end" | tr ' ' ',' | sed 's/,$//')"
        fi
    fi
    if [[ "$backend" == "x11" && $seen -eq 0 ]]; then
        evidence="$(xwininfo -root -tree 2>/dev/null | grep -F "$WINDOW_TITLE_PATTERN" | head -3 || true)"
        if [[ -n "$evidence" ]]; then
            seen=1
            first_at="≤${OBSERVE_SECONDS}"
        fi
    fi

    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null || true
        for _ in 1 2 3 4 5; do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.4
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "  SIGTERM 未生效，改用 SIGKILL"
            kill -9 "$pid" 2>/dev/null || true
        fi
    fi
    wait "$pid" 2>/dev/null || true

    CASE_LABEL+=("$label");       CASE_BACKEND+=("$backend")
    CASE_ALIVE+=("$alive");       CASE_WEBKIT+=("$webkit")
    CASE_WEBKIT_NOTE+=("$webkit_note")
    CASE_WINDOW+=("$seen");       CASE_WINDOW_AT+=("$first_at")
    CASE_EVIDENCE+=("$evidence"); CASE_LOG+=("$log")
    echo
}

case "$BACKEND" in
    auto)
        if [[ -n "${WAYLAND_DISPLAY:-}${DISPLAY:-}" ]]; then
            run_case "默认后端（WSLg 下即 Wayland）" "default"
        fi
        if [[ -n "${DISPLAY:-}" ]]; then
            run_case "强制 X11（GDK_BACKEND=x11）" "x11"
        fi
        ;;
    wayland)
        run_case "默认后端（WSLg 下即 Wayland）" "default"
        ;;
    x11)
        run_case "强制 X11（GDK_BACKEND=x11）" "x11"
        ;;
esac

if [[ ${#CASE_LABEL[@]} -eq 0 ]]; then
    section "没有可运行的路径"
    echo "没有可用的显示环境，或 --backend 指定的路径不适用当前环境。"
    exit 2
fi

section "结果汇总"

for i in "${!CASE_LABEL[@]}"; do
    echo "${CASE_LABEL[$i]}："
    if [[ ${CASE_ALIVE[$i]} -eq 1 ]]; then
        echo "  进程存活至观察窗结束：是"
    else
        echo "  进程存活至观察窗结束：否（提前退出）"
    fi
    if [[ ${CASE_WEBKIT[$i]} -eq 1 ]]; then
        echo "  WebKit 子进程：是（${CASE_WEBKIT_NOTE[$i]}）"
    else
        echo "  WebKit 子进程：否（webview 未真正开始加载页面）"
    fi
    if [[ ${CASE_BACKEND[$i]} == "x11" ]]; then
        if [[ ${CASE_WINDOW[$i]} -eq 1 ]]; then
            echo "  X 窗口断言（标题含 \"$WINDOW_TITLE_PATTERN\"）：是（第 ${CASE_WINDOW_AT[$i]}s 起）"
        else
            echo "  X 窗口断言（标题含 \"$WINDOW_TITLE_PATTERN\"）：否"
        fi
    else
        echo "  X 窗口断言：不适用（窗口在 Weston 里，xwininfo 看不到）"
    fi
    if [[ -n "${CASE_EVIDENCE[$i]}" ]]; then
        echo "  xwininfo -root -tree 片段："
        echo "${CASE_EVIDENCE[$i]}" | sed 's/^/    /'
    fi
    if [[ -s "${CASE_LOG[$i]}" ]]; then
        echo "  进程输出（前 40 行）："
        head -40 "${CASE_LOG[$i]}" | sed 's/^/    /'
    else
        echo "  进程输出：空（预期如此，库不往 stdout 打日志）"
    fi
    echo
done

# 判定：每条路径都要「进程存活 + 拉起 WebKit 子进程」；X11 路径另外要求窗口断言成立
exit_code=0
for i in "${!CASE_LABEL[@]}"; do
    if [[ ${CASE_ALIVE[$i]} -ne 1 || ${CASE_WEBKIT[$i]} -ne 1 ]]; then
        exit_code=1
    fi
    if [[ ${CASE_BACKEND[$i]} == "x11" && ${CASE_WINDOW[$i]} -ne 1 ]]; then
        exit_code=1
    fi
done

if [[ $exit_code -eq 0 ]]; then
    echo "机器判定：PASS（各路径进程存活 + WebKit 子进程已拉起；X11 路径另有窗口断言）"
    echo "待人工确认：窗口内徽章是否为「IPC 已连接」——写结论时必须带上人眼观察结果"
else
    echo "机器判定：FAIL"
    for i in "${!CASE_LABEL[@]}"; do
        if [[ ${CASE_ALIVE[$i]} -ne 1 ]]; then
            echo "  - ${CASE_LABEL[$i]}：进程在观察窗结束前就退出了"
        fi
        if [[ ${CASE_WEBKIT[$i]} -ne 1 ]]; then
            echo "  - ${CASE_LABEL[$i]}：全程没有出现 WebKit 子进程（webview 没有真正开始加载页面）"
        fi
        if [[ ${CASE_BACKEND[$i]} == "x11" && ${CASE_WINDOW[$i]} -ne 1 ]]; then
            echo "  - ${CASE_LABEL[$i]}：未枚举到标题含 \"$WINDOW_TITLE_PATTERN\" 的 X 窗口"
        fi
    done
fi

echo
echo "日志副本：${CASE_LOG[*]}"

exit $exit_code
