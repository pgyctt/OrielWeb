#!/usr/bin/env bash
#
# verify-linux.sh —— Linux 后端的完整验证（自检 + 外壳取证 + 运行取证）。
#
# 三平台的验证脚本命名与职责**一一对应**：
#   tools/verify-linux.sh        ← 本文件（bash）
#   tools/verify-windows.ps1     ← PowerShell
#   tools/verify-macos.sh        ← bash
# 每个平台一个脚本、内部按阶段分节；CI 里各自只调用一行。
#
# 为什么合成一个脚本（而不是拆成 verify-linux-selftest / -shell / -smoke 三个）：
# 拆开后三个文件的名字与另外两个平台对不上，而"哪个平台该跑什么"要跨三个文件去拼。
# 阶段划分仍在脚本内部保留（下面用 section 标出），失败时逐阶段给结论，
# 需要单独跑某一阶段时用 --stages 选择。
#
# ============================ 三个阶段 ============================
#
# 【自检】机器断言（结论行判定）
#   nav ipc capability multiwindow scheme threading tray tray-js geometry geometry-frameless
#   theme（深浅两条路径必须给出**不同**结论）
#   exit / exit-timeout（子进程；**同时**判结论行与退出码）
#   single-instance（双进程：第二个应立即成功退出并通知首实例）
#
# 【外壳】托盘与通知的取证
#   1. 托盘：能创建、能设进一份覆盖各形态的菜单（分隔线/勾选/禁用/子菜单/role），进程不崩；
#   2. 通知能力被**如实报告**：有客户端就报 true，没有就报 false（而不是假装成功）；
#   3. 有客户端时，通知真的走到了会话总线上的通知服务，且标题与正文**逐字符正确**；
#   4. Shell：外部打开成功，且 file:/裸路径/javascript: 都被白名单拒绝；
#   5. 内建右键菜单的默认策略是 Editing；开机自启形成闭环（启用→查得到→禁用→查不到）。
#   第 1 条能证明的只有"API 通路可用 + 不崩"：图标是否真的出现在托盘区需要人眼
#   （GNOME Shell 需要 AppIndicator 扩展、Wayland 会话多数不显示），见 docs/ROADMAP.md。
#
# 【运行】真实运行取证（观察窗 + 截图）
#   机器可判定：
#     1) 进程存活到观察窗结束（启动即崩会立刻暴露）；
#     2) 拉起 WebKit 子进程（WebKitWebProcess / WebKitNetworkProcess）——它只在 webview
#        真的开始加载页面时才出现，是"窗口已创建且 webview 在工作"的宿主侧硬证据；
#     3) 强制 GDK_BACKEND=x11 时，存在标题含 "Oriel Demo" 的 X 窗口。
#   只能人眼判定：页面渲染与中文是否正常，以及页面右下角徽章是否从「IPC 连接中…」变为
#   「IPC 已连接」（该徽章由 wwwroot/app.js 在启动时自动发起一次 todo.list 往返后改写；
#   库不往 stdout 打日志，因此 IPC 往返成功与否**没有**宿主侧可观测信号）。
#   脚本因此在窗口出现后截两张图（窗口图 + 整屏图）作 artifact，供人眼确认。
#
#   为什么跑两条路径：WSLg 下 GTK 默认走 Wayland，窗口注册在 Weston 合成器而非 XWayland，
#   xwininfo 枚举不到——"窗口是否存在"无法用 X 工具机器判定。因此默认后端那次只断言
#   "存活 + WebKit 子进程"，再补一次 GDK_BACKEND=x11 让窗口落到 XWayland 做窗口断言。
#   两条路径观察的是同一份页面，人眼结论可互相印证。
#
# ============================ 判定口径 ============================
# 自检阶段以日志里的**结论行**为准（`XXX-SELFTEST: PASS`）：各平台的应用退出方式不同，
# 退出码未必可靠地表达自检结果——macOS 上就出现过"结论 FAIL 却返回 0"。
# **例外是 exit / exit-timeout**：那两项的退出码本身就是被验证的契约（正常退出必须 0、
# 看门狗强杀必须 ForceExitCode=2），因此同时判结论行与退出码。
#
# ============================ 用法 ============================
#   ./tools/verify-linux.sh                      # 三个阶段全跑（自己发布 AOT 产物）
#   ./tools/verify-linux.sh --no-publish         # 复用已有产物（CI 用这条）
#   ./tools/verify-linux.sh --stages selftest    # 只跑自检（逗号分隔：selftest,shell,smoke,all）
#   ./tools/verify-linux.sh --seconds 60         # 运行取证每条路径的观察窗（默认 30 秒）
#   ./tools/verify-linux.sh --backend x11        # 运行取证只跑强制 X11 那条路径
#   ./tools/verify-linux.sh --rid linux-arm64
#   ./tools/verify-linux.sh --demo PATH          # 指定可执行产物
#   ./tools/verify-linux.sh --out DIR            # 日志与截图目录（默认 <仓库根>/linux-verify-out）
#   ./tools/verify-linux.sh --app-arg --hidden   # 透传参数给 demo（可重复）
#
# 无头环境：没有 DISPLAY/WAYLAND_DISPLAY 且装了 xvfb-run 时，脚本会**用 xvfb-run 重新执行
# 自己**（带重入哨兵），本地与 CI 的调用方式因此一致。
#
# 系统依赖（需要 sudo，本脚本只检测并提示，不自动安装）：
#   sudo apt-get install -y libwebkit2gtk-4.1-dev libgtk-3-dev x11-utils zlib1g-dev clang
#     libwebkit2gtk-4.1-dev 运行期与编译期的 WebKitGTK 4.1
#     libgtk-3-dev          GTK3 头文件与库
#     x11-utils             xwininfo/xprop，用于枚举 X 窗口与读窗口图标
#     zlib1g-dev / clang    Native AOT 的原生工具链前置
#   可选：imagemagick（import 截图）、libnotify-bin（真 notify-send）、python3-dbus/python3-gi
#         （通知服务替身与假通知服务；缺了则通知投递那条断言跳过，但"如实报告能力"仍会判）
#
# .NET 10 SDK：Ubuntu 官方 apt 源没有该版本，用官方脚本装到用户目录：
#   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
# 装好后本脚本会自动发现 $HOME/.dotnet/dotnet，也可显式指定 DOTNET=/path/to/dotnet
#
# 退出码：0 = 全部通过；1 = 有断言未通过；2 = 环境不满足（缺依赖/缺显示/缺产物）。
#
# 写作约定（CI 上真实踩到过）：变量一律写成 ${VAR} 形式。紧贴中文/全角字符的 $VAR
# 在非 UTF-8 locale 下（GitHub runner 就是）会被 bash 连同多字节字符一起当作变量名，
# 配合 set -u 直接 unbound variable 退出——本地 UTF-8 环境跑不出来，只在 CI 上炸。

set -uo pipefail

ORIG_ARGS=("$@")

RID="${RUNTIME_ID:-linux-x64}"
CONFIG="${CONFIG:-Release}"
OBSERVE_SECONDS=30
DO_PUBLISH=1
BACKEND="auto"          # auto | wayland | x11
WINDOW_TITLE_PATTERN="Oriel Demo"
OUT=""
DEMO_EXE=""
APP_ARGS=()
STAGES="all"            # all | 逗号分隔的 selftest,shell,smoke

FAILURES=0
FAIL_DETAIL=""

usage() {
    # 打印开头的注释块：从第 2 行到 set -uo pipefail 之前，去掉注释符
    sed -n '2,/^set -uo pipefail/p' "$0" | sed '$d' | sed 's/^#\{1,2\} \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --seconds)    OBSERVE_SECONDS="$2"; shift 2 ;;
        --rid)        RID="$2"; shift 2 ;;
        --backend)    BACKEND="$2"; shift 2 ;;
        --out)        OUT="$2"; shift 2 ;;
        --demo)       DEMO_EXE="$2"; shift 2 ;;
        --stages)     STAGES="$2"; shift 2 ;;
        --app-arg)    APP_ARGS+=("$2"); shift 2 ;;
        --no-publish) DO_PUBLISH=0; shift ;;
        -h|--help)    usage; exit 0 ;;
        *)            echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$BACKEND" in
    auto|wayland|x11) ;;
    *) echo "未知 --backend：${BACKEND}（可选 auto / wayland / x11）" >&2; exit 2 ;;
esac

# 阶段选择：all 或逗号分隔的子集
stage_enabled() {
    [[ "$STAGES" == "all" ]] && return 0
    [[ ",${STAGES}," == *",$1,"* ]]
}

for stage in ${STAGES//,/ }; do
    case "$stage" in
        all|selftest|shell|smoke) ;;
        *) echo "未知 --stages 取值：${stage}（可选 all / selftest / shell / smoke）" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO_ROOT/linux-verify-out}"
mkdir -p "$OUT"
DEMO_PROJECT="$REPO_ROOT/samples/OrielDemo/OrielDemo.csproj"
PUBLISH_DIR="$REPO_ROOT/samples/OrielDemo/bin/$CONFIG/net10.0/$RID/publish"
DEMO_EXE="${DEMO_EXE:-$PUBLISH_DIR/OrielDemo}"

section() { printf '\n=== %s ===\n' "$1"; }
fail() { echo "  [失败] $1"; FAILURES=$((FAILURES + 1)); FAIL_DETAIL="${FAIL_DETAIL:+${FAIL_DETAIL} }$1"; }
pass() { echo "  [通过] $1"; }

# ---------------------------------------------------------------------------
# 无头环境自举：没有显示时用 xvfb-run 重新执行自己
# ---------------------------------------------------------------------------
# 用环境变量做重入哨兵：没有它的话会递归地套下去。
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" && -z "${ORIEL_VERIFY_XVFB:-}" ]]; then
    if command -v xvfb-run >/dev/null 2>&1; then
        echo "没有显示环境：用 xvfb-run 重新执行本脚本（CI 上由调用方套，这里是本地兜底）"
        export ORIEL_VERIFY_XVFB=1
        exec xvfb-run -a bash "$0" "${ORIG_ARGS[@]}"
    fi
    echo "警告：没有 DISPLAY/WAYLAND_DISPLAY 也没有 xvfb-run，GTK 初始化会失败" >&2
fi

# ---------------------------------------------------------------------------
section "环境检查"
# ---------------------------------------------------------------------------

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
DOTNET="${DOTNET:-}"
if [[ -z "$DOTNET" ]]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET="dotnet"
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET="$HOME/.dotnet/dotnet"
    fi
fi

echo "发行版      : $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" || echo 未知)"
echo "内核        : $(uname -r)"
echo "架构 / RID  : $(uname -m) / ${RID}"
echo "阶段        : ${STAGES}"
echo "DISPLAY     : ${DISPLAY:-<空>}（WSLg 通常为 :0；WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-<空>}）"
echo "dotnet      : ${DOTNET:-<未找到>}"
echo "产物        : ${DEMO_EXE}"
echo "日志目录    : ${OUT}"

if [[ -n "$DOTNET" ]]; then
    echo "dotnet 版本 : $("$DOTNET" --version)"
fi

# ---------------------------------------------------------------------------
# 窗口管理器探测
# ---------------------------------------------------------------------------
# 有几条自检断言**必须有窗口管理器**才成立：
#   · 最小化（gtk_window_iconify 要被 WM 收进任务栏，IsMinimized 才会变 true）
#   · 关掉一个窗口后焦点回到另一个（前台窗口由 WM 指派）
#   · 「点关闭」走标准路径（gtk_window_close 在无 WM 时不产生完整的 delete 流程）
# CI 的 xvfb 里**没有 WM**，所以那几条验的是环境而不是库。探测结果交给 demo：
# 无 WM 时自检把这几条打印成 SKIP（可见、但不计入失败）——
# 把"环境不足"报成红灯会训练人忽略红灯，比跳过更糟。
#
# 判据用 EWMH 的 _NET_SUPPORTING_WM_CHECK：有值说明有符合 EWMH 的 WM 在跑。
export ORIEL_HAS_WM=0
if [[ -n "${DISPLAY:-}" ]] && command -v xprop >/dev/null 2>&1; then
    if xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | grep -q 'window id'; then
        export ORIEL_HAS_WM=1
    fi
fi
echo "窗口管理器  : $([[ "$ORIEL_HAS_WM" == "1" ]] && echo 有 || echo 无（依赖 WM 的断言将 SKIP）)"

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

if [[ -z "$DOTNET" ]]; then
    section "缺少 .NET 10 SDK"
    echo "安装到用户目录（无需 sudo）："
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
    exit 2
fi

if [[ ! -f "$DEMO_PROJECT" ]]; then
    echo "找不到示例项目：${DEMO_PROJECT}" >&2
    exit 2
fi

# ---------------------------------------------------------------------------
section "AOT 发布（${RID}）"
# ---------------------------------------------------------------------------
if [[ "$DO_PUBLISH" -eq 1 ]]; then
    echo "命令：${DOTNET} publish samples/OrielDemo -c ${CONFIG} -r ${RID}"
    # /mnt 下的 Windows 工作区跨文件系统，首次发布可能较慢
    if ! "$DOTNET" publish "$DEMO_PROJECT" -c "$CONFIG" -r "$RID" -v minimal; then
        echo "发布失败" >&2
        exit 1
    fi
else
    echo "（--no-publish：复用已有产物）"
fi

if [[ ! -x "$DEMO_EXE" ]]; then
    section "找不到可执行产物"
    echo "期望路径：${DEMO_EXE}"
    echo "请先不带 --no-publish 运行本脚本，或用 --demo 指定，或确认 RID 是否正确。"
    exit 2
fi

# ===========================================================================
# 阶段 1：机器断言自检
# ===========================================================================
if stage_enabled selftest; then

# GNU timeout 兜底：自检自身有看门狗（nav 90s / ipc 30s），但没有的话一次卡死就会吃掉
# 整个 job 的配额。超时（124）会被当成"没有结论行"→ 失败，这是对的。
MODE_TIMEOUT=150

# run_mode <模式> <结论行正则> [期望退出码]
# 给了期望退出码就**同时**判它（只用于 exit / exit-timeout：退出码本身是契约的一部分）。
run_mode() {
    local mode="$1" pattern="$2" expected="${3:-}"
    local log="${OUT}/selftest-${mode}.log"
    local code verdict

    timeout "${MODE_TIMEOUT}" "$DEMO_EXE" --selftest "$mode" >"$log" 2>&1
    code=$?
    verdict="$(grep -E "$pattern" "$log" | tail -1 || true)"

    echo "----- ${mode}（退出码 ${code}${expected:+，期望 ${expected}}）${verdict:-无结论行} -----"
    sed 's/^/  /' "$log" || true

    if [[ "$verdict" != *PASS* ]]; then
        local tail
        tail="$(grep -v '^[[:space:]]*$' "$log" | tail -n 2 | tr '\n' ' ' | tr -s ' ')"
        fail "selftest ${mode} 未通过（结论行：${verdict:-无}）：${tail:0:200}"
    elif [[ -n "$expected" && "$code" -ne "$expected" ]]; then
        fail "selftest ${mode} 的进程退出码应为 ${expected}，实际 ${code}"
    fi
}

section "自检：机器断言（结论行判定）"
for mode in nav ipc capability multiwindow scheme threading tray tray-js geometry geometry-frameless windowevents event close window windownew monitors menu menubar api; do
    case "$mode" in
        threading) pattern='^THREADING-SELFTEST: ' ;;
        tray)      pattern='^TRAY-SELFTEST: ' ;;
        tray-js)   pattern='^TRAY-JS-SELFTEST: ' ;;
        geometry)  pattern='^GEOMETRY-SELFTEST: ' ;;
        geometry-frameless) pattern='^GEOMETRY-SELFTEST: ' ;;
        windowevents) pattern='^WINDOWEVENTS-SELFTEST: ' ;;
        event)     pattern='^EVENT-SELFTEST: ' ;;
        close)     pattern='^CLOSE-SELFTEST: ' ;;
        window)    pattern='^WINDOW-SELFTEST: ' ;;
        windownew) pattern='^WINDOWNEW-SELFTEST: ' ;;
        monitors)  pattern='^MONITORS-SELFTEST: ' ;;
        menu)      pattern='^MENU-SELFTEST: ' ;;
        menubar)   pattern='^MENUBAR-SELFTEST: ' ;;
        api)       pattern='^API-SELFTEST: ' ;;
        *)         pattern='^(NAV|IPC|CAPABILITY|MULTIWINDOW|SCHEME)-SELFTEST: ' ;;
    esac
    run_mode "$mode" "$pattern"
done

section "自检：主题（深浅两条路径，GTK_THEME 造值）"
# 用 GTK_THEME（GTK 认可的入口）分别跑。只跑一次只能证明"读得到主题"，
# 证明不了"深浅判得对"；同一进程内又改不了系统主题，所以只能这样造值。
pkill -x OrielDemo 2>/dev/null || true
theme_light=""
theme_dark=""
for pair in "light:" "dark:Adwaita:dark"; do
    label="${pair%%:*}"
    value="${pair#*:}"
    log="${OUT}/theme-${label}.log"

    GTK_THEME="$value" timeout "${MODE_TIMEOUT}" "$DEMO_EXE" --selftest theme >"$log" 2>&1
    code=$?
    verdict="$(grep -E '^THEME-SELFTEST: ' "$log" | tail -1 || true)"

    echo "----- theme=${label}（GTK_THEME='${value}'，退出码 ${code}）${verdict:-无结论行} -----"
    sed 's/^/  /' "$log" || true

    if [[ "$verdict" != *PASS* ]]; then
        fail "selftest theme 未通过（${label}，结论行：${verdict:-无}）"
    fi

    if [[ "$label" = "light" ]]; then theme_light="$verdict"; else theme_dark="$verdict"; fi
done

if [[ -n "$theme_light" && "$theme_light" = "$theme_dark" ]]; then
    fail "深浅两次得到相同结论（${theme_light}）——主题判定没有真正区分两条路径"
fi

section "自检：退出语义（结论行 + 退出码）"
# 这两项必须子进程取证：看门狗超时走 Environment.Exit，会把测试运行器一起带走。
#  · exit          —— 正常退出，退出码必须是 0（看门狗不该在正常路径上强杀）。
#  · exit-timeout  —— 看门狗超时强杀，退出码必须是 ForceExitCode（默认 2）。
# 只看结论行会漏掉"看门狗没真的强杀"（进程正常退出了）；
# 只看退出码会漏掉"事件没触发"。两者缺一都不算通过。
run_mode "exit"         '^EXIT-SELFTEST: '         0
run_mode "exit-timeout" '^EXIT-TIMEOUT-SELFTEST: ' 2

section "自检：单实例（双进程）"
# 这一项没法单进程自证：起两个进程，第二个应立即成功退出并通知首实例，
# 第一个应收到激活请求（首实例自带 15 秒看门狗，不会挂住）。
pkill -x OrielDemo 2>/dev/null || true

timeout "${MODE_TIMEOUT}" "$DEMO_EXE" --selftest single-instance >"${OUT}/si-primary.log" 2>&1 &
si_primary_pid=$!
sleep 5
si_start=$(date +%s)
timeout "${MODE_TIMEOUT}" "$DEMO_EXE" --selftest single-instance >"${OUT}/si-secondary.log" 2>&1
si_secondary_code=$?
si_elapsed=$(( $(date +%s) - si_start ))
wait "$si_primary_pid" 2>/dev/null
si_primary_code=$?

echo "  [single-instance] 第二实例 退出码=${si_secondary_code}（${si_elapsed}s） 首实例 退出码=${si_primary_code}"
sed 's/^/    /' "${OUT}/si-primary.log" || true
sed 's/^/    /' "${OUT}/si-secondary.log" || true

if [[ "$si_secondary_code" -ne 0 ]]; then
    fail "第二个实例退出码非 0（${si_secondary_code}）"
elif [[ "$si_elapsed" -ge 10 ]]; then
    fail "第二个实例没有立即退出（${si_elapsed}s）"
elif ! grep -q 'SINGLE-INSTANCE-SECONDARY' "${OUT}/si-secondary.log"; then
    fail "第二个实例没有通知首实例"
elif ! grep -q 'SINGLE-INSTANCE: PASS' "${OUT}/si-primary.log"; then
    fail "首实例没有收到激活请求"
else
    pass "单实例：第二个实例立即退出并通知了首实例"
fi

fi  # stage selftest

# ===========================================================================
# 阶段 2：外壳取证（托盘 + 通知 + Shell + 自启）
# ===========================================================================
if stage_enabled shell; then

section "外壳取证（托盘 + 通知 + Shell + 自启）"
# 通知与 demo 必须在**同一个 dbus 会话**里，否则 demo 的 notify-send 找不到那个假服务。
# 没有 python3-dbus 时退化成"只跑托盘自检"（通知投递那条断言跳过）。
APP_LOG="${OUT}/app.log"
SERVICE_LOG="${OUT}/notification-service.log"

if command -v python3 >/dev/null 2>&1 && python3 -c "import dbus" >/dev/null 2>&1; then
    HAVE_DBUS_PYTHON=1
else
    HAVE_DBUS_PYTHON=0
fi

# 通知客户端：优先真 notify-send，缺了就用替身补齐
FAKE_BIN=""
if command -v notify-send >/dev/null 2>&1; then
    CLIENT_DESC="真 notify-send（$(command -v notify-send)）"
    EXPECT_SUPPORTED=1
elif [[ "$HAVE_DBUS_PYTHON" == "1" ]]; then
    FAKE_BIN="$(mktemp -d /tmp/oriel-fakebin-XXXXXX)"
    install -m 755 "$REPO_ROOT/tools/fake-notify-send.py" "$FAKE_BIN/notify-send"
    export PATH="$FAKE_BIN:$PATH"
    CLIENT_DESC="替身 notify-send（环境未装 libnotify，用 tools/fake-notify-send.py 补齐 D-Bus 调用）"
    EXPECT_SUPPORTED=1
else
    CLIENT_DESC="无（既没有 notify-send，也没有 python3-dbus）"
    EXPECT_SUPPORTED=0
fi

echo "  通知客户端：${CLIENT_DESC}"
echo "  期望 NOTIFICATIONS-SUPPORTED：${EXPECT_SUPPORTED}"

# xdg-open 替身：把"打开了什么"记进文件，让"确实调用了系统默认程序"成为可断言的事实。
# 无头环境里真 xdg-open 会尝试拉起浏览器并失败，替身反而更干净（与通知那条同一手法）。
if [[ -z "$FAKE_BIN" ]]; then
    FAKE_BIN="$(mktemp -d /tmp/oriel-fakebin-XXXXXX)"
fi
cat > "$FAKE_BIN/xdg-open" <<EOF
#!/bin/sh
echo "XDG-OPEN \$@" >> "${OUT}/xdg-open.log"
exit 0
EOF
chmod +x "$FAKE_BIN/xdg-open"
export PATH="$FAKE_BIN:$PATH"
echo "  xdg-open：替身（记录参数到 ${OUT}/xdg-open.log）"

if [[ "$HAVE_DBUS_PYTHON" == "1" ]]; then
    echo "  在会话总线里启动假通知服务并运行 --selftest shell"
    DEMO_EXE="$DEMO_EXE" OUT_APP="$APP_LOG" OUT_SERVICE="$SERVICE_LOG" \
        SERVICE_PY="$REPO_ROOT/tools/fake-notification-service.py" \
        dbus-run-session -- bash -c '
            python3 "$SERVICE_PY" >"$OUT_SERVICE" 2>&1 &
            service_pid=$!
            for _ in $(seq 1 50); do
                grep -q "FAKE-NOTIFICATION-SERVICE-READY" "$OUT_SERVICE" 2>/dev/null && break
                sleep 0.1
            done
            timeout 60 "$DEMO_EXE" --selftest shell >"$OUT_APP" 2>&1
            echo $? >"$OUT_APP.exit"
            sleep 0.5
            kill "$service_pid" 2>/dev/null
            wait "$service_pid" 2>/dev/null
        ' 2>"${OUT}/dbus-run-session.log"
else
    echo "  无 python3-dbus：跳过通知服务，只跑托盘自检"
    timeout 60 "$DEMO_EXE" --selftest shell >"$APP_LOG" 2>&1
    echo $? >"$APP_LOG.exit"
fi

SHELL_EXIT_CODE="$(cat "$APP_LOG.exit" 2>/dev/null || echo "?")"
echo "  进程退出码：${SHELL_EXIT_CODE}"
echo "  --- 应用输出 ---"
grep -E "shell-selftest|SHELL-SELFTEST" "$APP_LOG" || echo "  （无自检输出）"

if [[ "$SHELL_EXIT_CODE" == "0" ]]; then
    pass "进程以退出码 0 结束（托盘创建 + 菜单构建 + 通知投递都没把进程带崩）"
else
    fail "外壳自检的进程退出码为 ${SHELL_EXIT_CODE}（应为 0）"
fi

if grep -q "SHELL-SELFTEST: PASS" "$APP_LOG"; then
    pass "外壳自检结论为 PASS"
else
    fail "外壳自检未打印 PASS"
fi

reported="$(grep -oE "NOTIFICATIONS-SUPPORTED: (true|false)" "$APP_LOG" | tail -1 | awk '{print $2}')"
if [[ "$EXPECT_SUPPORTED" == "1" && "$reported" == "true" ]]; then
    pass "通知能力报告为 true（与环境中存在客户端一致）"
elif [[ "$EXPECT_SUPPORTED" == "0" && "$reported" == "false" ]]; then
    pass "通知能力报告为 false（环境确实没有客户端——如实报告，而不是假装成功）"
else
    fail "通知能力报告为 '${reported}'，与环境预期（${EXPECT_SUPPORTED}）不符"
fi

if [[ "$reported" == "true" ]]; then
    if grep -q "NOTIFY-CALL" "$SERVICE_LOG" 2>/dev/null; then
        call="$(grep "NOTIFY-CALL" "$SERVICE_LOG" | tail -1)"
        echo "  通知服务收到：${call}"
        if grep -q "title=OrielWeb 自检" "$SERVICE_LOG" && grep -q "body=看到这条通知说明通知投递可用。" "$SERVICE_LOG"; then
            pass "通知真的到达了会话总线上的通知服务，且标题与正文逐字符正确"
        else
            fail "通知到达了，但标题或正文与预期不符"
        fi
    else
        fail "报告支持通知，但通知服务没有收到任何 Notify 调用"
    fi
else
    echo "  （跳过通知投递断言：本环境没有通知客户端）"
fi

# 内建右键菜单：断言**默认策略**。过滤本身发生在渲染引擎内部（无头环境看不到菜单内容），
# 但"默认就是只留剪切/复制/粘贴"是需求的核心，也是这里唯一可机器判定的一环。
if grep -q "CONTEXT-MENU-POLICY: Editing" "$APP_LOG"; then
    pass "内建右键菜单的默认策略是 Editing（只留剪切/复制/粘贴）"
else
    fail "内建右键菜单的默认策略不是 Editing——默认过滤是需求，被改动必须同步更新本断言"
fi

# 开机自启：三平台都能形成闭环，所以这里断言的是**四个 true**（不是"看平台而定"）。
autostart="$(grep -oE "AUTOSTART: enable=\w+ seen=\w+ disable=\w+ gone=\w+" "$APP_LOG" | tail -1)"
if [[ "$autostart" == "AUTOSTART: enable=True seen=True disable=True gone=True" ]]; then
    pass "开机自启形成闭环：启用 → 查得到 → 禁用 → 查不到（自检已清理，不留自启项）"
else
    fail "开机自启闭环不成立（实际：${autostart:-无输出}）"
fi

if grep -q "AUTOSTART-DESKTOP-OK: true" "$APP_LOG"; then
    pass "写出的 .desktop 内容逐项正确（Desktop Entry 头、带引号的 Exec、参数、X-GNOME-Autostart-enabled）"
else
    fail "autostart 文件内容不符合预期"
fi

# Shell：既要"真的调用了系统默认程序"，也要"危险目标一次都没调出去"
if grep -q "SHELL-OPEN-EXTERNAL: true" "$APP_LOG" && grep -q "SHELL-REJECTED-DANGEROUS: true" "$APP_LOG"; then
    pass "Shell 自检：外部打开成功，且 file:/裸路径/javascript: 都被拒绝"
else
    fail "Shell 自检未通过（外部打开或白名单拒绝不符合预期）"
fi

if grep -q "https://example.com/oriel-selftest" "${OUT}/xdg-open.log" 2>/dev/null; then
    pass "OpenExternal 真的把 URL 交给了系统默认程序（替身 xdg-open 收到了它）"
else
    fail "OpenExternal 没有把 URL 交给系统默认程序"
fi

if grep -qE "file://|/etc/passwd|javascript:" "${OUT}/xdg-open.log" 2>/dev/null; then
    fail "危险目标被交给了系统默认程序——白名单没拦住"
else
    pass "白名单是有效的：替身一次都没收到 file:、裸路径或 javascript:"
fi

fi  # stage shell

# ===========================================================================
# 阶段 3：运行取证（观察窗 + 截图）
# ===========================================================================
if stage_enabled smoke; then

section "运行取证（每条路径观察窗 ${OBSERVE_SECONDS}s）"
echo "产物：${DEMO_EXE}"
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

# 截图取证（ImageMagick 的 import）：按窗口 id 抓窗口，或抓整个 root 作对照。
# 只有 X11 路径有窗口可抓（Wayland 的窗口在 Weston 里，不在 X 树中）。
# import 缺失或失败只提示不失败——截图是给人眼看的补充证据，不参与机器判定。
capture_shot() {
    local target="$1" path="$2"
    if ! command -v import >/dev/null 2>&1; then
        echo "  （未装 ImageMagick 的 import，跳过截图；apt install imagemagick）"
        return 1
    fi
    if import -window "$target" "$path" 2>/dev/null; then
        echo "  已截图：${path}"
        return 0
    fi
    echo "  截图失败（target=${target}）"
    return 1
}

CASE_LABEL=(); CASE_BACKEND=(); CASE_ALIVE=(); CASE_WEBKIT=(); CASE_WEBKIT_NOTE=()
CASE_WINDOW=(); CASE_WINDOW_AT=(); CASE_EVIDENCE=(); CASE_LOG=(); CASE_SHOTS=(); CASE_MAP=(); CASE_ICON=()

run_case() {
    local label="$1" backend="$2"
    local log pid second
    log="$(mktemp -t oriel-case-XXXXXX.log)"

    echo "--- ${label} ---"
    if [[ "$backend" == "x11" ]]; then
        echo "启动：GDK_BACKEND=x11 ${DEMO_EXE} ${APP_ARGS[*]:-}"
        GDK_BACKEND=x11 "$DEMO_EXE" ${APP_ARGS[@]+"${APP_ARGS[@]}"} >"$log" 2>&1 &
    else
        echo "启动：${DEMO_EXE} ${APP_ARGS[*]:-}（后端交给 GDK 自行选择，WSLg 下即 Wayland）"
        "$DEMO_EXE" ${APP_ARGS[@]+"${APP_ARGS[@]}"} >"$log" 2>&1 &
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
                echo "  [${second}s] WebKit 子进程出现（${webkit_note}）"
            fi
        fi

        # 只有 X11 后端才可能被 xwininfo 枚举到（Wayland 窗口在 Weston 里，X 树里没有）
        if [[ "$backend" == "x11" && $seen -eq 0 ]]; then
            # 末尾的 || true 是必需的：pipefail 下 grep 无匹配会让整条管道返回非零。
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

    # 截图取证：窗口图（聚焦页面）+ 整屏图（对照窗口位置与尺寸）。
    # 放在收尾复检之后、杀进程之前——此刻页面已渲染完、徽章也已更新。
    local shots=""
    local map_state=""
    local icon_evidence=""
    if [[ $seen -eq 1 && -n "$evidence" ]]; then
        local window_id
        window_id="$(echo "$evidence" | head -1 | awk '{print $1}')"
        # 窗口映射状态：隐藏启动（--app-arg --hidden）时应为 IsUnMapped，正常启动为 IsViewable。
        # 只记录不判定——期望值取决于调用方传了什么参数。
        map_state="$(xwininfo -id "$window_id" 2>/dev/null | grep -i 'Map State' | sed 's/^ *//' || true)"
        # 窗口图标：X11 下只有真的设了图标才会有 _NET_WM_ICON 属性（即 WithIcon 的机器可判定证据）
        icon_evidence="$(xprop -id "$window_id" _NET_WM_ICON 2>/dev/null | head -c 120 || true)"
        # 截图只是补充证据，失败（或缺 ImageMagick）绝不能把整个验证流程带崩。
        capture_shot "$window_id" "${OUT}/shot-${backend}-window.png" \
            && shots="$shots${OUT}/shot-${backend}-window.png " || true
        capture_shot root "${OUT}/shot-${backend}-root.png" \
            && shots="$shots${OUT}/shot-${backend}-root.png " || true
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
    CASE_SHOTS+=("$shots")
    CASE_MAP+=("$map_state")
    CASE_ICON+=("$icon_evidence")
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

section "运行取证结果汇总"

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
            echo "  X 窗口断言（标题含 \"${WINDOW_TITLE_PATTERN}\"）：是（第 ${CASE_WINDOW_AT[$i]}s 起）"
        else
            echo "  X 窗口断言（标题含 \"${WINDOW_TITLE_PATTERN}\"）：否"
        fi
    else
        echo "  X 窗口断言：不适用（窗口在 Weston 里，xwininfo 看不到）"
    fi
    if [[ -n "${CASE_EVIDENCE[$i]}" ]]; then
        echo "  xwininfo -root -tree 片段："
        echo "${CASE_EVIDENCE[$i]}" | sed 's/^/    /'
    fi
    if [[ -n "${CASE_MAP[$i]}" ]]; then
        echo "  窗口映射状态：${CASE_MAP[$i]}"
    fi
    if [[ -n "${CASE_ICON[$i]}" ]]; then
        echo "  窗口图标（_NET_WM_ICON）：${CASE_ICON[$i]}"
    else
        echo "  窗口图标（_NET_WM_ICON）：无（未传 --icon 时属正常）"
    fi
    if [[ -n "${CASE_SHOTS[$i]}" ]]; then
        echo "  截图（人眼判定用）："
        for shot in ${CASE_SHOTS[$i]}; do
            echo "    ${shot}"
        done
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
for i in "${!CASE_LABEL[@]}"; do
    if [[ ${CASE_ALIVE[$i]} -ne 1 || ${CASE_WEBKIT[$i]} -ne 1 ]]; then
        fail "${CASE_LABEL[$i]}：进程未存活到观察窗结束，或全程没有出现 WebKit 子进程"
    fi
    if [[ ${CASE_BACKEND[$i]} == "x11" && ${CASE_WINDOW[$i]} -ne 1 ]]; then
        fail "${CASE_LABEL[$i]}：未枚举到标题含 \"${WINDOW_TITLE_PATTERN}\" 的 X 窗口"
    fi
done

if [[ $FAILURES -eq 0 ]]; then
    pass "运行取证：各路径进程存活 + WebKit 子进程已拉起（X11 路径另有窗口断言）"
fi

fi  # stage smoke

# ===========================================================================
section "结论"
# ===========================================================================

if [[ "$FAILURES" -eq 0 ]]; then
    echo "全部通过（阶段：${STAGES}）"
    echo "日志与截图：${OUT}"
    echo
    echo "仍需人眼确认（本脚本无法机器判定）：页面渲染与中文是否正常、"
    echo "页面右下角徽章是否为「IPC 已连接」、托盘图标是否真的出现在托盘区。"
    exit 0
fi

echo "有 ${FAILURES} 项未通过："
echo "${FAIL_DETAIL}" | tr ' ' '\n' | grep -v '^$' | sed 's/^/  - /'
echo "::error title=Linux 验证::${FAIL_DETAIL}"
echo "日志与截图：${OUT}"
exit 1
