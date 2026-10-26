// `@orielweb/api/window`：单窗口的全部方法（状态、几何、外观、光标、任务栏、事件、关闭接管）。
//
// 危险项都做了"自动复原"：禁用输入、隐藏光标、忽略鼠标这些一旦留在原地，
// 验证台自己就点不动了。复原用的是页面定时器——窗口被禁用不影响页面里的 setTimeout。

import {
    Effect,
    EffectState,
    ProgressBarStatus,
    UserAttentionType,
    availableMonitors,
    currentMonitor,
    cursorPosition,
    getAllWindows,
    getByLabel,
    getCurrentWindow,
    getFocusedWindow,
    monitorFromPoint,
    primaryMonitor,
} from "../vendor/orielweb-api/window.js";
import { LogicalPosition, LogicalSize } from "../vendor/orielweb-api/dpi.js";
import { Image } from "../vendor/orielweb-api/image.js";
import { log } from "../ui.js";
import { demoPngPath } from "./common.js";

const win = getCurrentWindow();
const unlisten = new Map();
let closeIntercept = false;
let closeHooked = false;

async function hookClose() {
    if (closeHooked) {
        return;
    }
    closeHooked = true;
    await win.onCloseRequested((event) => {
        if (closeIntercept) {
            closeIntercept = false;
            event.preventDefault();
            log("onCloseRequested：已 preventDefault，窗口没有关闭。", "warn");
        } else {
            log("onCloseRequested：未阻止，窗口即将关闭。", "warn");
        }
    });
}

/** 执行一个会改变窗口状态的操作，并在 ms 毫秒后复原。 */
async function withRevert(change, revert, ms, note) {
    await change();
    setTimeout(() => { void revert().catch(() => {}); }, ms);
    return note;
}

export default {
    id: "window",
    title: "Window 窗口",
    module: "@orielweb/api/window",
    blurb:
        "一个窗口的全部方法。标「平台差异」的项在别的平台会 not_supported，标「自动复原」的项"
        + "会在几秒后自己恢复——否则验证台会把自己禁用掉。",
    groups: [
        {
            title: "身份与状态（只读）",
            items: [
                { name: "label", note: "同步属性，不是方法（对标 Tauri 的 Window.label）。", run: () => win.label },
                { name: "title()", run: () => win.title() },
                { name: "theme()", run: () => win.theme() },
                { name: "scaleFactor()", run: () => win.scaleFactor() },
                { name: "isMaximized()", run: () => win.isMaximized() },
                { name: "isMinimized()", run: () => win.isMinimized() },
                { name: "isFocused()", run: () => win.isFocused() },
                { name: "isVisible()", run: () => win.isVisible() },
                { name: "isFullscreen()", run: () => win.isFullscreen() },
                { name: "isAlwaysOnTop()", run: () => win.isAlwaysOnTop() },
                { name: "isDecorated()", run: () => win.isDecorated() },
                { name: "isResizable()", run: () => win.isResizable() },
                { name: "isMaximizable()", run: () => win.isMaximizable() },
                { name: "isMinimizable()", run: () => win.isMinimizable() },
                { name: "isClosable()", run: () => win.isClosable() },
                { name: "isEnabled()", run: () => win.isEnabled() },
            ],
        },
        {
            title: "显隐与焦点",
            items: [
                {
                    name: "hide() → 1.5s → show()",
                    run: () => withRevert(() => win.hide(), () => win.show(), 1500, "已隐藏，1.5 秒后自动显示"),
                },
                { name: "show()", run: () => win.show() },
                { name: "setFocus()", run: () => win.setFocus() },
                { name: "center()", run: () => win.center() },
                { name: "minimize() → unminimize()", run: async () => { await win.minimize(); setTimeout(() => void win.unminimize(), 800); return "已最小化，0.8 秒后还原"; } },
                { name: "unminimize()", run: () => win.unminimize() },
                { name: "maximize()", run: () => win.maximize() },
                { name: "unmaximize()", run: () => win.unmaximize() },
                { name: "toggleMaximize()", run: () => win.toggleMaximize() },
                {
                    name: "requestUserAttention(Critical)",
                    note: "任务栏闪烁 / Dock 弹跳；Linux 两档无区别。",
                    run: async () => {
                        await win.requestUserAttention(UserAttentionType.Critical);
                        setTimeout(() => void win.requestUserAttention(null), 1200);
                        return "已请求注意，1.2 秒后取消";
                    },
                },
                { name: "requestUserAttention(null)", run: () => win.requestUserAttention(null) },
            ],
        },
        {
            title: "尺寸与位置",
            items: [
                { name: "innerSize()", run: () => win.innerSize() },
                { name: "outerSize()", run: () => win.outerSize() },
                { name: "innerPosition()", run: () => win.innerPosition() },
                { name: "outerPosition()", run: () => win.outerPosition() },
                { name: "setSize(LogicalSize 1000×700)", run: () => win.setSize(new LogicalSize(1000, 700)) },
                { name: "setPosition(LogicalPosition 120,90)", run: () => win.setPosition(new LogicalPosition(120, 90)) },
                { name: "setMinSize(LogicalSize 820×600)", run: () => win.setMinSize(new LogicalSize(820, 600)) },
                { name: "setMaxSize(LogicalSize 1600×1200)", run: () => win.setMaxSize(new LogicalSize(1600, 1200)) },
                {
                    name: "setSizeConstraints({ minWidth, minHeight })",
                    note: "整体替换语义：没给的字段会被取消。",
                    run: () => win.setSizeConstraints({ minWidth: 900, minHeight: 640 }),
                },
                { name: "setSizeConstraints(null)（全部取消）", run: () => win.setSizeConstraints(null) },
            ],
        },
        {
            title: "外观",
            items: [
                { name: "setTitle('Oriel API Demo — 已改名')", run: () => win.setTitle("Oriel API Demo — 已改名") },
                { name: "setTitle('Oriel API Demo')（复原）", run: () => win.setTitle("Oriel API Demo") },
                { name: "setResizable(false) → true", run: () => withRevert(() => win.setResizable(false), () => win.setResizable(true), 2000, "2 秒内不可拉伸") },
                { name: "setFullscreen(true) → 2s → false", run: () => withRevert(() => win.setFullscreen(true), () => win.setFullscreen(false), 2000, "2 秒后退出全屏") },
                { name: "toggleFullscreen()", run: () => win.toggleFullscreen() },
                { name: "setAlwaysOnTop(true) → 2s → false", run: () => withRevert(() => win.setAlwaysOnTop(true), () => win.setAlwaysOnTop(false), 2000, "2 秒内置顶") },
                { name: "toggleAlwaysOnTop()", run: () => win.toggleAlwaysOnTop() },
                { name: "setAlwaysOnBottom(true) → 2s → false", run: () => withRevert(() => win.setAlwaysOnBottom(true), () => win.setAlwaysOnBottom(false), 2000, "2 秒内置底") },
                {
                    name: "setDecorations(false) → 4s → true",
                    badge: "自动复原",
                    note: "去掉系统标题栏（含边框）。4 秒后恢复，避免验证台变成关不掉的窗口。",
                    run: () => withRevert(() => win.setDecorations(false), () => win.setDecorations(true), 4000, "已去掉装饰，4 秒后恢复"),
                },
                { name: "setBackgroundColor('#1b2a4a')", note: "页面渲染前的底色；再设成浅色即可看出差别。", run: () => win.setBackgroundColor("#1b2a4a") },
                { name: "setBackgroundColor('#ffffff')", run: () => win.setBackgroundColor("#ffffff") },
                { name: "setTheme('dark')", run: () => win.setTheme("dark") },
                { name: "setTheme(null)（跟随系统）", run: () => win.setTheme(null) },
                {
                    name: "setIcon(Image.fromPath('app.png'))",
                    note: "把随产物分发的图标读进页面再交给宿主（走 Image 路径）。",
                    run: async () => {
                        const image = await Image.fromPath(await demoPngPath());
                        await win.setIcon(image);
                        return `已设置图标 ${image.width}×${image.height}`;
                    },
                },
                {
                    name: "setOverlayIcon('app.png')",
                    badge: "平台差异",
                    note: "Windows 任务栏角标；macOS 用 setBadgeCount/setBadgeLabel。",
                    run: async () => win.setOverlayIcon(await demoPngPath()),
                },
                { name: "setOverlayIcon()（清除）", run: () => win.setOverlayIcon() },
                {
                    name: "setShadow(false) → true",
                    badge: "平台差异",
                    run: () => withRevert(() => win.setShadow(false), () => win.setShadow(true), 2000, "2 秒无阴影"),
                },
                {
                    name: "setTitleBarStyle('overlay')",
                    badge: "macOS 专属",
                    note: "Windows/Linux 抛 not_supported（等价能力是 Frameless + 自绘标题栏）。",
                    run: () => win.setTitleBarStyle("overlay"),
                },
                {
                    name: "setEffects({ Mica, Sidebar })",
                    badge: "平台差异",
                    note: "Windows 11 / macOS 有实现；Linux 整条命令 not_supported。",
                    run: () => win.setEffects({ effects: [Effect.Mica, Effect.Sidebar], state: EffectState.Active }),
                },
                { name: "clearEffects()", run: () => win.clearEffects() },
                { name: "setContentProtected(true) → false", run: () => withRevert(() => win.setContentProtected(true), () => win.setContentProtected(false), 2000, "2 秒内禁止截屏（平台支持时）") },
                { name: "setSimpleFullscreen(true) → false", badge: "平台差异", run: () => withRevert(() => win.setSimpleFullscreen(true), () => win.setSimpleFullscreen(false), 2000, "2 秒简化全屏") },
                {
                    name: "setFullscreenOnMonitor(primaryMonitor.position)",
                    badge: "平台差异",
                    run: async () => {
                        const monitor = await primaryMonitor();
                        if (monitor === null) {
                            return "没有主显示器信息";
                        }
                        await win.setFullscreenOnMonitor(monitor.position);
                        setTimeout(() => void win.setFullscreen(false), 2000);
                        return "已在那台显示器上全屏，2 秒后退出";
                    },
                },
                {
                    name: "setFocusable(false) → 1.5s → true",
                    badge: "自动复原",
                    note: "窗口不可聚焦（不接收键盘焦点）。1.5 秒后恢复。",
                    run: () => withRevert(() => win.setFocusable(false), () => win.setFocusable(true), 1500, "1.5 秒内不可聚焦"),
                },
                {
                    name: "setEnabled(false) → 1.5s → true",
                    badge: "自动复原",
                    note: "窗口整体禁用（不接收输入）。1.5 秒后恢复，否则验证台点不动。",
                    run: () => withRevert(() => win.setEnabled(false), () => win.setEnabled(true), 1500, "1.5 秒内窗口被禁用"),
                },
                {
                    name: "setMaximizable(false) → 2s → true",
                    badge: "自动复原",
                    run: () => withRevert(() => win.setMaximizable(false), () => win.setMaximizable(true), 2000, "2 秒内不能最大化"),
                },
                {
                    name: "setMinimizable(false) → 2s → true",
                    badge: "自动复原",
                    run: () => withRevert(() => win.setMinimizable(false), () => win.setMinimizable(true), 2000, "2 秒内不能最小化"),
                },
                {
                    name: "setClosable(false) → 2s → true",
                    badge: "自动复原",
                    note: "2 秒内标题栏关闭按钮不可用。",
                    run: () => withRevert(() => win.setClosable(false), () => win.setClosable(true), 2000, "2 秒内不可关闭"),
                },
            ],
        },
        {
            title: "光标",
            items: [
                { name: "setCursorIcon('hand')", run: () => win.setCursorIcon("hand") },
                { name: "setCursorIcon('default')（复原）", run: () => win.setCursorIcon("default") },
                {
                    name: "setCursorVisible(false) → 2s → true",
                    badge: "自动复原",
                    run: () => withRevert(() => win.setCursorVisible(false), () => win.setCursorVisible(true), 2000, "2 秒内不显示光标"),
                },
                {
                    name: "setCursorGrab(true) → 2s → false",
                    badge: "自动复原",
                    run: () => withRevert(() => win.setCursorGrab(true), () => win.setCursorGrab(false), 2000, "2 秒内抓取光标"),
                },
                { name: "setCursorPosition(LogicalPosition 200,200)", run: () => win.setCursorPosition(new LogicalPosition(200, 200)) },
                {
                    name: "setIgnoreCursorEvents(true) → 2s → false",
                    badge: "自动复原",
                    note: "鼠标穿透（点击落到后面的窗口）。2 秒后恢复，否则按钮点不到。",
                    run: () => withRevert(() => win.setIgnoreCursorEvents(true), () => win.setIgnoreCursorEvents(false), 2000, "2 秒内鼠标穿透"),
                },
                {
                    name: "startResizeDragging('SouthEast')",
                    badge: "需要鼠标",
                    note: "进入原生拉伸循环；接下来移动鼠标，按 Esc 结束。",
                    run: () => win.startResizeDragging("SouthEast"),
                },
                {
                    name: "startDragging()",
                    badge: "需要鼠标",
                    note: "进入原生窗口拖动；按住鼠标移动即可。",
                    run: () => win.startDragging(),
                },
            ],
        },
        {
            title: "任务栏与工作区",
            items: [
                {
                    name: "setSkipTaskbar(true) → 4s → false",
                    badge: "自动复原",
                    run: () => withRevert(() => win.setSkipTaskbar(true), () => win.setSkipTaskbar(false), 4000, "已从任务栏隐藏，4 秒后恢复"),
                },
                {
                    name: "setVisibleOnAllWorkspaces(true)",
                    badge: "平台差异",
                    run: () => win.setVisibleOnAllWorkspaces(true),
                },
                { name: "setVisibleOnAllWorkspaces(false)", badge: "平台差异", run: () => win.setVisibleOnAllWorkspaces(false) },
                {
                    name: "setProgressBar('indeterminate')",
                    badge: "Windows 专属",
                    run: () => win.setProgressBar(ProgressBarStatus.Indeterminate),
                },
                {
                    name: "setProgressBar({ normal, 42 })",
                    badge: "Windows 专属",
                    run: () => win.setProgressBar({ status: ProgressBarStatus.Normal, progress: 42 }),
                },
                { name: "setProgressBar('none')（清除）", run: () => win.setProgressBar(ProgressBarStatus.None) },
                { name: "setBadgeCount(3)", badge: "macOS/Linux", run: () => win.setBadgeCount(3) },
                { name: "setBadgeCount()（清除）", run: () => win.setBadgeCount() },
                { name: "setBadgeLabel('API')", badge: "macOS/Linux", run: () => win.setBadgeLabel("API") },
                { name: "setBadgeLabel()（清除）", run: () => win.setBadgeLabel() },
            ],
        },
        {
            title: "窗口列表与显示器",
            items: [
                { name: "getAllWindows()", run: async () => (await getAllWindows()).map((w) => w.label) },
                { name: "getByLabel('main')", run: async () => (await getByLabel("main"))?.label ?? null },
                { name: "getFocusedWindow()", run: async () => (await getFocusedWindow())?.label ?? null },
                { name: "currentMonitor()", run: () => currentMonitor() },
                { name: "primaryMonitor()", run: () => primaryMonitor() },
                { name: "availableMonitors()", run: () => availableMonitors() },
                { name: "monitorFromPoint(100, 100)", run: () => monitorFromPoint(100, 100) },
                { name: "cursorPosition()", run: () => cursorPosition() },
            ],
        },
        {
            title: "窗口事件",
            note: "订阅后动一下窗口就能在日志里看到。退订在最后一条。",
            items: [
                { name: "onResized()", run: () => subscribe("resized", () => win.onResized((event) => log(`resized → ${JSON.stringify(event.payload)}`, "info"))) },
                { name: "onMoved()", run: () => subscribe("moved", () => win.onMoved((event) => log(`moved → ${JSON.stringify(event.payload)}`, "info"))) },
                { name: "onFocusChanged()", run: () => subscribe("focus", () => win.onFocusChanged((event) => log(`focus → ${String(event.payload)}`, "info"))) },
                { name: "onThemeChanged()", run: () => subscribe("theme", () => win.onThemeChanged((event) => log(`theme → ${String(event.payload)}`, "info"))) },
                { name: "onScaleChanged()", run: () => subscribe("scale", () => win.onScaleChanged((event) => log(`scale → ${JSON.stringify(event.payload)}`, "info"))) },
                { name: "退订全部", run: () => { for (const fn of unlisten.values()) fn(); unlisten.clear(); return "已退订"; } },
            ],
        },
        {
            title: "关闭接管与销毁",
            items: [
                { name: "订阅 onCloseRequested()", run: async () => { await hookClose(); return "已订阅；点关闭按钮时会看到日志"; } },
                { name: "拦截下一次关闭", run: async () => { await hookClose(); closeIntercept = true; return "下一次关闭会被 preventDefault()"; } },
                {
                    name: "close()",
                    badge: "危险",
                    confirm: "关闭本窗口（会先走 onCloseRequested）？",
                    run: () => win.close(),
                },
                {
                    name: "destroy()",
                    badge: "危险",
                    danger: true,
                    confirm: "强制销毁窗口（绕过关闭接管），验证台会消失。继续？",
                    run: () => win.destroy(),
                },
            ],
        },
    ],
};

async function subscribe(key, fn) {
    if (unlisten.has(key)) {
        return `已订阅（${key}）`;
    }
    // onXxx 返回 Promise<UnlistenFn>（也可能直接是 thenable 函数），await 两种都成立。
    const off = await fn();
    unlisten.set(key, off);
    return `已订阅 ${key}`;
}
