#!/usr/bin/env bash
# Linux 外壳能力（托盘 + 通知）的取证脚本。
#
# 断言（每条都是机器可判定的）：
#   1. 托盘：能创建、能设进一份覆盖各形态的菜单（分隔线 / 勾选 / 禁用 / 子菜单 / role），进程不崩；
#   2. 通知能力被**如实报告**：有客户端就报 true，没有就报 false（而不是假装成功）；
#   3. 有客户端时，通知真的走到了会话总线上的通知服务，且标题与正文逐字符正确。
#
# 第 1 条能证明的只有"API 通路可用 + 不崩"：图标是否真的出现在托盘区需要人眼
# （GNOME Shell 需要 AppIndicator 扩展、Wayland 会话多数不显示），见 docs/ROADMAP.md 的待真机清单。
#
# 用法：
#   bash tools/verify-linux-shell.sh                 # 先发布 demo 再运行
#   bash tools/verify-linux-shell.sh --no-publish     # 复用已有产物
#   bash tools/verify-linux-shell.sh --out DIR        # 把日志留到 DIR
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${CONFIG:-Release}"
RID="${RID:-linux-x64}"
OUT_DIR=""
DO_PUBLISH=1

# dotnet 的定位：PATH 里没有时退回 SDK 的常见安装位置（WSL 里常是 ~/.dotnet/dotnet）
if [[ -z "${DOTNET:-}" ]]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET="dotnet"
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET="$HOME/.dotnet/dotnet"
    else
        echo "找不到 dotnet（可用 DOTNET=/path/to/dotnet 指定）" >&2
        exit 1
    fi
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-publish) DO_PUBLISH=0; shift ;;
        --out) OUT_DIR="$2"; shift 2 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
done

if [[ -z "$OUT_DIR" ]]; then
    OUT_DIR="$(mktemp -d /tmp/oriel-shell-XXXXXX)"
fi
mkdir -p "$OUT_DIR"

# 没有显示时 GTK 初始化会失败、demo 立刻退出——那不是代码问题，所以先明确诊断，
# 免得把"缺 xvfb"误读成"托盘实现有 bug"。
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
    echo "警告：环境里没有 DISPLAY / WAYLAND_DISPLAY，GTK 无法初始化，demo 会立即失败。"
    echo "      需要图形环境，或用 xvfb-run 包一层："
    echo "        xvfb-run -a bash tools/verify-linux-shell.sh --no-publish"
    echo "      （CI 的 smoke-linux 步骤就是这么调的）"
    echo
fi

APP_LOG="$OUT_DIR/app.log"
SERVICE_LOG="$OUT_DIR/notification-service.log"

echo "=== Linux 外壳取证（托盘 + 通知）==="
echo "证据目录：$OUT_DIR"

failures=0
fail() { echo "  [失败] $1"; failures=$((failures + 1)); }
pass() { echo "  [通过] $1"; }

# ---- 1. 发布 demo ----
PUBLISH_DIR="$REPO_ROOT/samples/OrielDemo/bin/$CONFIG/net10.0/$RID/publish"
EXE="$PUBLISH_DIR/OrielDemo"

if [[ "$DO_PUBLISH" == "1" ]]; then
    echo "--- 发布 demo（$RID / $CONFIG）---"
    if ! "$DOTNET" publish "$REPO_ROOT/samples/OrielDemo/OrielDemo.csproj" -c "$CONFIG" -r "$RID" -v minimal >"$OUT_DIR/publish.log" 2>&1; then
        echo "发布失败，日志见 $OUT_DIR/publish.log" >&2
        exit 1
    fi
fi

if [[ ! -x "$EXE" ]]; then
    echo "找不到可执行文件：$EXE（去掉 --no-publish 试一次）" >&2
    exit 1
fi

# ---- 2. 通知客户端：优先真 notify-send，缺了就用替身补齐 ----
if command -v python3 >/dev/null 2>&1 && python3 -c "import dbus" >/dev/null 2>&1; then
    HAVE_DBUS_PYTHON=1
else
    HAVE_DBUS_PYTHON=0
fi

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

echo "--- 通知客户端：$CLIENT_DESC"
echo "--- 期望 NOTIFICATIONS-SUPPORTED：$EXPECT_SUPPORTED"

# ---- 3. 在会话总线里跑自检（通知服务与 demo 同一个 dbus session）----
if [[ "$HAVE_DBUS_PYTHON" == "1" ]]; then
    echo "--- 启动假通知服务并运行 --shell-selftest ---"
    EXE="$EXE" OUT_APP="$APP_LOG" OUT_SERVICE="$SERVICE_LOG" SERVICE_PY="$REPO_ROOT/tools/fake-notification-service.py" \
        dbus-run-session -- bash -c '
            python3 "$SERVICE_PY" >"$OUT_SERVICE" 2>&1 &
            service_pid=$!
            for _ in $(seq 1 50); do
                grep -q "FAKE-NOTIFICATION-SERVICE-READY" "$OUT_SERVICE" 2>/dev/null && break
                sleep 0.1
            done
            timeout 60 "$EXE" --shell-selftest >"$OUT_APP" 2>&1
            echo $? >"$OUT_APP.exit"
            sleep 0.5
            kill "$service_pid" 2>/dev/null
            wait "$service_pid" 2>/dev/null
        ' 2>"$OUT_DIR/dbus-run-session.log"
else
    echo "--- 无 python3-dbus：跳过通知服务，只跑托盘自检 ---"
    timeout 60 "$EXE" --shell-selftest >"$APP_LOG" 2>&1
    echo $? >"$APP_LOG.exit"
fi

EXIT_CODE="$(cat "$APP_LOG.exit" 2>/dev/null || echo "?")"
echo "--- 进程退出码：$EXIT_CODE"

# ---- 4. 断言 ----
echo "--- 应用输出 ---"
grep -E "shell-selftest|SHELL-SELFTEST" "$APP_LOG" || echo "（无自检输出）"

if [[ "$EXIT_CODE" == "0" ]]; then
    pass "进程以退出码 0 结束（托盘创建 + 菜单构建 + 通知投递都没把进程带崩）"
else
    fail "进程退出码为 $EXIT_CODE（应为 0）"
fi

if grep -q "SHELL-SELFTEST: PASS" "$APP_LOG"; then
    pass "自检结论为 PASS"
else
    fail "自检未打印 PASS"
fi

reported="$(grep -oE "NOTIFICATIONS-SUPPORTED: (true|false)" "$APP_LOG" | tail -1 | awk '{print $2}')"
if [[ "$EXPECT_SUPPORTED" == "1" && "$reported" == "true" ]]; then
    pass "通知能力报告为 true（与环境中存在客户端一致）"
elif [[ "$EXPECT_SUPPORTED" == "0" && "$reported" == "false" ]]; then
    pass "通知能力报告为 false（环境确实没有客户端——如实报告，而不是假装成功）"
else
    fail "通知能力报告为 '$reported'，与环境预期（$EXPECT_SUPPORTED）不符"
fi

if [[ "$reported" == "true" ]]; then
    if grep -q "NOTIFY-CALL" "$SERVICE_LOG" 2>/dev/null; then
        call="$(grep "NOTIFY-CALL" "$SERVICE_LOG" | tail -1)"
        echo "  通知服务收到：$call"
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

# 全局快捷键：Linux 实现是"如实返回不支持"（X11 可做但未实现、Wayland 无解），
# 所以这里断言 false。将来若实现了 X11 的 XGrabKey，这条断言要改成"与平台能力一致"。
shortcut="$(grep -oE "GLOBAL-SHORTCUT-REGISTERED: (true|false)" "$APP_LOG" | tail -1 | awk '{print $2}')"
if [[ -z "$shortcut" ]]; then
    fail "没有找到 GLOBAL-SHORTCUT-REGISTERED 输出行"
elif [[ "$shortcut" == "false" ]]; then
    pass "全局快捷键在 Linux 上如实报告不支持（返回 false，而不是假装注册成功）"
else
    fail "全局快捷键报告为 true——Linux 实现当前不支持，若已实现 X11 版本请同步更新本断言"
fi

if grep -q "徽章 API 已调用" "$APP_LOG"; then
    pass "徽章 API 调用未抛异常（Linux 上是文档化的 no-op）"
else
    fail "徽章 API 没有被调用到"
fi

# ---- 5. 结论 ----
echo
if [[ "$failures" == "0" ]]; then
    echo "机器判定：PASS"
    echo "边界说明：托盘图标的**可见性**、菜单的实际外观、通知横幅的展示都无法在无头环境判定，"
    echo "          需人眼确认（见 docs/ROADMAP.md 的待真机清单）。"
    exit 0
fi

echo "机器判定：FAIL（$failures 项未通过）"
exit 1
