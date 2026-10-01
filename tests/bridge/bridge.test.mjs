// 桥接 JS 单元测试（Node 内置 test runner）。
//
// 作用：三平台桥接脚本此前零覆盖，P0 级缺陷（ready 因 TDZ 变成 rejected Promise、
// orielready 事件死于时序）正因如此长期存活。D-6 之后三平台共用同一份模板
// （src/OrielWeb/Bridge/oriel-bridge.js），本测试直接读该模板、按各平台差异做
// 占位符替换后注入最小 window/document 桩执行，断言"就绪 / 事件 / 往返 / 回执 /
// 错误 / 超时"六类行为，并参数化跑全部三个平台。
//
// 桩里还有一个小 DOM 模型（元素、选择器、事件、window 上的监听），用来覆盖
// **标题栏拖动接管**：库在注入脚本里处理"移动超阈值才拖动""双击的第二下不进入拖动"
// "让开按钮与 data-oriel-no-drag"这些判断，而它们全是页面侧逻辑，只能在这里断言。
//
// 运行：node --test tests/bridge/bridge.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../../', import.meta.url));
const templatePath = 'src/OrielWeb/Bridge/oriel-bridge.js';

/**
 * 三平台的差异项。post 必须与 OrielBridgeTemplate.Create 的调用点一致
 * （src/OrielWeb/Bridge/OrielBridgeTemplate.cs、OrielBridgeJs.cs、MacOS/LinuxBridgeJs.cs）。
 */
const bridges = [
    { platform: 'windows', channel: 'chrome', post: 'window.chrome.webview.postMessage(obj)' },
    { platform: 'macos', channel: 'webkit', post: 'window.webkit.messageHandlers.oriel.postMessage(JSON.stringify(obj))' },
    { platform: 'linux', channel: 'webkit', post: 'window.webkit.messageHandlers.oriel.postMessage(JSON.stringify(obj))' },
];

const template = readFileSync(join(repoRoot, templatePath), 'utf8');

/** 测试用的注入版本（对应 C# 侧 OrielBridgeTemplate 的 VersionLiteral）。 */
const injectedVersion = '9.8.7';

/** 测试用的令牌（对应 C# 侧 OrielIpcToken.Generate：32 位小写十六进制）。 */
const injectedToken = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';

/** 测试用的可信来源前缀（对应 OrielIpcGuard 自动加入的内嵌资源虚拟主机）。 */
const trustedPrefixes = ['https://app.oriel/'];

/** 测试用的注入值（对应 C# 侧 OrielSystemSnapshot 与拖动默认值）。 */
const injectedDoubleClickMs = 477;
const injectedDragThresholdPx = 3;

/**
 * 复现 C# 侧的占位符替换，得到该平台真正被注入的脚本。
 * forwardConsole 对应 OrielWindowOptions.ConsoleForwarding（默认关闭）。
 */
function buildScript(
    bridge,
    {
        forwardConsole = false,
        token = injectedToken,
        trusted = trustedPrefixes,
        doubleClickMs = injectedDoubleClickMs,
        dragThresholdPx = injectedDragThresholdPx,
        dragSelector = '',
    } = {},
) {
    const script = template
        .replaceAll('__ORIEL_PLATFORM__', `'${bridge.platform}'`)
        .replaceAll('__ORIEL_POST__', bridge.post)
        .replaceAll('__ORIEL_CONSOLE_ENABLED__', forwardConsole ? 'true' : 'false')
        .replaceAll('__ORIEL_VERSION__', injectedVersion)
        .replaceAll('__ORIEL_TOKEN__', token)
        .replaceAll('__ORIEL_TRUSTED__', JSON.stringify(trusted))
        // 数值走 InvariantCulture（见 OrielBridgeTemplate.NumberLiteral），
        // 选择器走 JsonText.EncodeString（空选择器写作 ''）
        .replaceAll('__ORIEL_DOUBLE_CLICK_MS__', String(doubleClickMs))
        .replaceAll('__ORIEL_DRAG_THRESHOLD_PX__', String(dragThresholdPx))
        .replaceAll('__ORIEL_DRAG_SELECTOR__', dragSelector === '' ? "''" : JSON.stringify(dragSelector));
    assert.ok(!script.includes('__ORIEL_'), `${bridge.platform}：生成的脚本仍残留占位符`);
    return script;
}

/** 最小环境桩：按通道只提供该平台真实存在的 API。 */
function createEnvironment(channel) {
    const posted = [];
    const messageListeners = [];
    const domListeners = new Map();

    const windowStub = {};
    if (channel === 'chrome') {
        windowStub.chrome = {
            webview: {
                postMessage: (message) => posted.push(message),
                addEventListener: (type, listener) => {
                    if (type === 'message') messageListeners.push(listener);
                },
            },
        };
    } else {
        windowStub.webkit = {
            messageHandlers: {
                // macOS/Linux 版以 JSON 字符串投递，桩内还原为对象，便于统一断言
                oriel: { postMessage: (json) => posted.push(JSON.parse(json)) },
            },
        };
    }

    // 拖动接管那段逻辑要碰 DOM，所以桩里得有一个够用的元素模型：
    // 属性/标签/类/ id 选择器、事件、closest，以及 window 上的监听（拖动期间挂 mousemove/mouseup）。
    const elements = [];
    const windowListeners = new Map();

    function matchesSelector(element, selector) {
        return selector.split(',').some((raw) => {
            const part = raw.trim();
            if (!part) return false;
            if (part.startsWith('[') && part.endsWith(']')) return element.hasAttribute(part.slice(1, -1));
            if (part.startsWith('#')) return element.getAttribute('id') === part.slice(1);
            if (part.startsWith('.')) return (element.getAttribute('class') ?? '').split(/\s+/).includes(part.slice(1));
            return element.tagName === part.toUpperCase();
        });
    }

    class StubElement {
        constructor(tagName, attributes = {}) {
            this.tagName = tagName.toUpperCase();
            this.attributes = { ...attributes };
            this.listeners = new Map();
            this.parentElement = null;
            elements.push(this);
        }

        getAttribute(name) {
            return Object.hasOwn(this.attributes, name) ? this.attributes[name] : null;
        }

        hasAttribute(name) {
            return Object.hasOwn(this.attributes, name);
        }

        addEventListener(type, listener) {
            const list = this.listeners.get(type) ?? [];
            list.push(listener);
            this.listeners.set(type, list);
        }

        closest(selector) {
            for (let node = this; node; node = node.parentElement) {
                if (matchesSelector(node, selector)) return node;
            }
            return null;
        }

        /** 触发一个事件；缺省字段按"鼠标左键单击"补齐，用例只写自己关心的那些。 */
        fire(type, overrides = {}) {
            const event = {
                type,
                target: this,
                button: 0,
                detail: 1,
                timeStamp: 0,
                screenX: 0,
                screenY: 0,
                preventDefault() { },
                ...overrides,
            };
            for (const listener of this.listeners.get(type) ?? []) listener(event);
        }
    }

    const documentStub = {
        readyState: 'loading',
        addEventListener: (type, listener) => {
            const list = domListeners.get(type) ?? [];
            list.push(listener);
            domListeners.set(type, list);
        },
        dispatchEvent: (event) => {
            for (const listener of domListeners.get(event.type) ?? []) listener(event);
            return true;
        },
        querySelectorAll: (selector) => elements.filter((element) => matchesSelector(element, selector)),
    };

    windowStub.addEventListener = (type, listener) => {
        const list = windowListeners.get(type) ?? [];
        list.push(listener);
        windowListeners.set(type, list);
    };
    windowStub.removeEventListener = (type, listener) => {
        const list = windowListeners.get(type) ?? [];
        windowListeners.set(type, list.filter((candidate) => candidate !== listener));
    };
    // 拖动时页面会把这些坐标交给宿主（真实浏览器里都有）
    windowStub.screenX = 100;
    windowStub.screenY = 60;
    windowStub.outerWidth = 1024;
    windowStub.outerHeight = 768;
    windowStub.screen = { height: 1080 };

    // console 桩：console 转发 hook 会包装它，因此不能让它落到 Node 的全局 console 上
    // （否则会污染测试进程，且多平台用例会互相叠加包装）。记录调用以便断言"原行为保留"。
    const consoleCalls = [];
    const consoleStub = {};
    for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
        consoleStub[level] = (...args) => consoleCalls.push({ level, args });
    }

    return {
        window: windowStub,
        document: documentStub,
        console: consoleStub,
        consoleCalls,
        posted,
        messageListeners,
        domListeners,
        StubElement,
        elements,
        addElement: (tagName, attributes) => new StubElement(tagName, attributes),
        addChild: (parent, tagName, attributes) => {
            const child = new StubElement(tagName, attributes);
            child.parentElement = parent;
            return child;
        },
        fireWindow: (type, overrides = {}) => {
            const event = {
                type,
                button: 0,
                detail: 1,
                timeStamp: 0,
                screenX: 0,
                screenY: 0,
                preventDefault() { },
                ...overrides,
            };
            for (const listener of windowListeners.get(type) ?? []) listener(event);
        },
    };
}

/**
 * 加载桥接脚本。加载期的异常不抛出而是记录，便于单独断言"加载本身无异常"。
 *
 * location 必须作为参数注入：脚本用 location.href 判定来源是否可信，而 Node 里没有这个全局。
 */
function loadBridge(script, channel, { href = `${trustedPrefixes[0]}index.html` } = {}) {
    const env = createEnvironment(channel);
    env.location = { href };
    const EventStub = class { constructor(type) { this.type = type; } };
    try {
        new Function('window', 'document', 'Event', 'console', 'location', 'Element', script)(
            env.window, env.document, EventStub, env.console, env.location, env.StubElement);
    } catch (error) {
        env.loadError = error;
    }
    return env;
}

/** 触发文档事件（模拟浏览器在 DOMContentLoaded 时机调用已注册的监听器）。 */
function fireDomEvent(env, type) {
    const event = { type };
    for (const listener of env.domListeners.get(type) ?? []) listener(event);
}

/**
 * 投递命令回执。按桥接实际通道分发：
 * chrome.webview 走 message 事件；webkit 走宿主 evaluateJavaScript 调用 _onResult。
 */
function deliverResult(env, id, ok, payload) {
    if (env.messageListeners.length > 0) {
        for (const listener of env.messageListeners) {
            listener({
                data: {
                    __oriel: 'result',
                    id,
                    ok,
                    value: ok ? payload : undefined,
                    error: ok ? undefined : payload,
                },
            });
        }
        return;
    }
    env.window.oriel._onResult(id, ok, payload);
}

/**
 * 投递宿主推送的窗口事件（如 maximized）。通道同回执：chrome.webview 走 message 事件，
 * webkit 由宿主 EvaluateScript 调 _onEvent。
 */
function deliverEvent(env, name, value) {
    if (env.messageListeners.length > 0) {
        for (const listener of env.messageListeners) {
            listener({ data: { __oriel: 'event', name, value } });
        }
        return;
    }
    env.window.oriel._onEvent(name, value);
}

test('模板：占位符齐备', () => {
    assert.ok(template.includes('__ORIEL_PLATFORM__'), '模板缺少 __ORIEL_PLATFORM__');
    assert.ok(template.includes('__ORIEL_POST__'), '模板缺少 __ORIEL_POST__');
    assert.ok(template.includes('__ORIEL_CONSOLE_ENABLED__'), '模板缺少 __ORIEL_CONSOLE_ENABLED__');
    assert.ok(template.includes('__ORIEL_VERSION__'), '模板缺少 __ORIEL_VERSION__');
    assert.ok(template.includes('__ORIEL_TOKEN__'), '模板缺少 __ORIEL_TOKEN__');
    assert.ok(template.includes('__ORIEL_TRUSTED__'), '模板缺少 __ORIEL_TRUSTED__');
    assert.ok(template.includes('__ORIEL_DOUBLE_CLICK_MS__'), '模板缺少 __ORIEL_DOUBLE_CLICK_MS__');
    assert.ok(template.includes('__ORIEL_DRAG_THRESHOLD_PX__'), '模板缺少 __ORIEL_DRAG_THRESHOLD_PX__');
    assert.ok(template.includes('__ORIEL_DRAG_SELECTOR__'), '模板缺少 __ORIEL_DRAG_SELECTOR__');
    assert.ok(template.includes('window.__orielBridgeInstalled'), '模板缺少重复注入防护');
});

// ---- 安全模型（来源 / 令牌） ----

test('安全：不可信来源下桥接完全不安装', () => {
    // "远程页面不接 IPC"是结构性成立的：脚本自己先退出，而不是装上了再靠宿主侧拦。
    const env = loadBridge(
        buildScript(bridges[0]),
        bridges[0].channel,
        { href: 'https://evil.example/index.html' });

    assert.equal(env.loadError, undefined, `不可信来源下脚本不应抛异常：${env.loadError}`);
    assert.equal(env.window.oriel, undefined, '不可信来源下不应存在 window.oriel');
    assert.equal(env.window.__orielBridgeInstalled, undefined, '不可信来源下不应留下安装标记');
    assert.equal(env.posted.length, 0);
});

test('安全：来源按前缀比较，近似域名不算可信', () => {
    // 前缀是 'https://app.oriel/'（末尾带斜杠），所以把 host 拼进自己域名里这种样子不会命中
    const env = loadBridge(
        buildScript(bridges[0]),
        bridges[0].channel,
        { href: 'https://app.oriel.evil.com/index.html' });

    assert.equal(env.window.oriel, undefined);
});

test('安全：可信前缀为多份时，任一份命中即安装', () => {
    // AllowOrigin 追加的开发期来源（如 Vite dev server）走的是同一条判定
    const trusted = ['https://app.oriel/', 'http://localhost:5173/'];
    const env = loadBridge(
        buildScript(bridges[0], { trusted }),
        bridges[0].channel,
        { href: 'http://localhost:5173/index.html' });

    assert.equal(env.loadError, undefined, `桥接脚本加载抛异常：${env.loadError}`);
    assert.ok(env.window.oriel, '显式放行的来源应当装上桥接');
});

test('模板：版本号不得写死为字面量', () => {
    // oriel.version 是给页面做能力探测用的，写死会与包版本各自漂移。
    // 这里钉住"必须来自注入"：模板里不该出现形如 '0.1.0' 的裸版本字面量。
    assert.ok(!/version:\s*'\d+\.\d+\.\d+'/.test(template), '模板里的 version 被写死成了字面量');
});

for (const bridge of bridges) {
    const label = `[${bridge.platform}]`;

    test(`${label} postMessage：产生 __oriel:'message' 协议消息`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        env.window.oriel.postMessage('from-page', { n: 1 });
        assert.equal(env.posted.length, 1);
        assert.deepEqual(env.posted[0], {
            __oriel: 'message', name: 'from-page', payload: { n: 1 }, token: injectedToken,
        });
    });

    test(`${label} postMessage：未给 payload 时投 null、空 name 立即抛出`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        env.window.oriel.postMessage('bare');
        assert.deepEqual(env.posted[0], {
            __oriel: 'message', name: 'bare', payload: null, token: injectedToken,
        });
        assert.throws(() => env.window.oriel.postMessage(''), /postMessage/);
    });

    test(`${label} 安全：每条出站消息都带上注入的令牌`, async () => {
        // 令牌随脚本注入，宿主侧不匹配即丢弃（挡"不是本应用注入的脚本也往通道里塞消息"）。
        // 三种出站消息都要带——漏掉任何一种就是那一条通道没有门禁。
        const env = loadBridge(buildScript(bridge, { forwardConsole: true }), bridge.channel);
        env.window.oriel.postMessage('m', { n: 1 });
        const promise = env.window.oriel.invoke('x', {});
        deliverResult(env, 1, true, null);
        await promise;
        env.console.log('c');

        assert.ok(env.posted.length >= 3, `出站消息数量不足：${env.posted.length}`);
        for (const message of env.posted) {
            assert.equal(message.token, injectedToken, `${message.__oriel} 消息缺少令牌`);
        }
    });

    test(`${label} 安全：令牌来自注入而不是写死`, () => {
        const other = 'ffffffffffffffffffffffffffffffff';
        const env = loadBridge(buildScript(bridge, { token: other }), bridge.channel);
        env.window.oriel.postMessage('m');
        assert.equal(env.posted[0].token, other);
    });

    test(`${label} console 转发：开启后 console.* 送回宿主，原方法仍被调用`, () => {
        const env = loadBridge(buildScript(bridge, { forwardConsole: true }), bridge.channel);
        env.console.log('hello', 42);
        const messages = env.posted.filter((m) => m && m.__oriel === 'console');
        assert.equal(messages.length, 1, 'console.log 应产生一条 __oriel:console 消息');
        assert.equal(messages[0].level, 'log');
        assert.equal(messages[0].text, 'hello 42');
        assert.equal(env.consoleCalls.length, 1, '原 console 方法应仍被调用');
    });

    test(`${label} console 转发：未开启时不产生 console 消息`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        env.console.log('silent');
        assert.equal(env.posted.filter((m) => m && m.__oriel === 'console').length, 0);
    });

    test(`${label} 就绪：脚本加载无异常且 window.oriel.ready 可 await`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        assert.equal(env.loadError, undefined, `桥接脚本加载抛异常：${env.loadError}`);
        assert.ok(env.window.oriel, 'window.oriel 未安装');
        assert.equal(env.window.oriel.platform, bridge.platform);
        assert.equal(env.window.oriel.version, injectedVersion, 'oriel.version 未采用注入值');
        await env.window.oriel.ready;
    });

    // ---- 标题栏拖动接管（库实现，页面只标区域）----

    const dragCommand = bridge.platform === 'windows' ? 'win.drag' : 'win.dragStart';

    test(`${label} 拖动区域：DOMContentLoaded 时按属性扫描`, () => {
        // 页面只写一个属性（data-oriel-drag-region），拖动逻辑全在库里
        const env = loadBridge(buildScript(bridge), bridge.channel);
        env.addElement('div', { 'data-oriel-drag-region': '' });
        fireDomEvent(env, 'DOMContentLoaded');

        assert.equal(env.window.oriel.dragRegion(), 1, '应当扫描到 1 个拖动区域');
    });

    test(`${label} 拖动区域：也可由宿主指定选择器`, () => {
        const env = loadBridge(buildScript(bridge, { dragSelector: '#titlebar' }), bridge.channel);
        env.addElement('div', { id: 'titlebar' });

        assert.equal(env.window.oriel.dragRegion(), 1);
    });

    test(`${label} 拖动区域：重复登记不重复绑定`, () => {
        // SPA 里重新扫描是常事，重复绑定会让一次按下发起两次拖动
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', {});
        env.window.oriel.dragRegion(bar);
        env.window.oriel.dragRegion(bar);

        assert.equal(bar.listeners.get('mousedown').length, 1, '同一个元素不应重复绑定');
    });

    test(`${label} 拖动区域：移动超过阈值才发起拖动`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', { 'data-oriel-drag-region': '' });
        env.window.oriel.dragRegion();
        env.posted.length = 0;

        bar.fire('mousedown', { screenX: 10, screenY: 10 });
        // 阈值内的抖动不算拖动——否则"点一下标题栏"也会移动窗口
        env.fireWindow('mousemove', { screenX: 12, screenY: 11 });
        assert.equal(env.posted.length, 0, '阈值内不应发起拖动');

        env.fireWindow('mousemove', { screenX: 40, screenY: 10 });
        assert.equal(env.posted.at(-1).name, dragCommand);

        if (bridge.platform === 'windows') {
            return;
        }

        // 起点用**按下时**的位置而不是当前位置，否则窗口会在开始拖动那一刻跳一下
        assert.deepEqual(env.posted.at(-1).args, {
            px: 10, py: 10, wx: 100, wy: 60, ww: 1024, wh: 768, sh: 1080,
        });

        env.fireWindow('mousemove', { screenX: 60, screenY: 20 });
        const moved = env.posted.at(-1);
        assert.equal(moved.name, 'win.dragTo');
        assert.deepEqual(moved.args, { dx: 50, dy: 10 });

        env.fireWindow('mouseup', {});
        assert.equal(env.posted.at(-1).name, 'win.dragEnd');
    });

    test(`${label} 拖动区域：双击的第二下不进入拖动，双击触发最大化`, () => {
        // 这正是"必须同步判定"的那个场景：拖动一旦开始，第二次点击就到不了页面
        // （Windows 的模态循环吞掉它 / Wayland 下指针被合成器 grab）
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', { 'data-oriel-drag-region': '' });
        env.window.oriel.dragRegion();
        env.posted.length = 0;

        bar.fire('mousedown', { screenX: 10, screenY: 10, timeStamp: 0 });
        bar.fire('mousedown', { screenX: 11, screenY: 10, timeStamp: 120, detail: 2 });
        env.fireWindow('mousemove', { screenX: 300, screenY: 300 });
        assert.equal(env.posted.length, 0, '双击的第二下不应发起拖动');

        bar.fire('dblclick', { screenX: 10, screenY: 10 });
        assert.equal(env.posted.at(-1).name, 'win.toggleMaximize');
    });

    test(`${label} 拖动区域：间隔之外的第二下仍算新的拖动`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', { 'data-oriel-drag-region': '' });
        env.window.oriel.dragRegion();
        env.posted.length = 0;

        bar.fire('mousedown', { screenX: 10, screenY: 10, timeStamp: 0 });
        bar.fire('mousedown', { screenX: 10, screenY: 10, timeStamp: injectedDoubleClickMs + 1 });
        env.fireWindow('mousemove', { screenX: 60, screenY: 10 });

        assert.equal(env.posted.at(-1).name, dragCommand);
    });

    test(`${label} 拖动区域：按钮与 no-drag 上的按下不接管`, () => {
        // 标题栏上的最小化/关闭按钮要能正常点击——否则按钮会变成"点不动"
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', { 'data-oriel-drag-region': '' });
        env.window.oriel.dragRegion();
        const button = env.addChild(bar, 'button', {});
        const panel = env.addChild(bar, 'div', { 'data-oriel-no-drag': '' });
        env.posted.length = 0;

        button.fire('mousedown', { screenX: 10, screenY: 10 });
        panel.fire('mousedown', { screenX: 20, screenY: 20 });
        env.fireWindow('mousemove', { screenX: 300, screenY: 300 });

        assert.equal(env.posted.length, 0, '按钮与 no-drag 区域内的按下不应发起拖动');
    });

    test(`${label} 拖动区域：右键与中键不接管`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const bar = env.addElement('div', { 'data-oriel-drag-region': '' });
        env.window.oriel.dragRegion();
        env.posted.length = 0;

        bar.fire('mousedown', { button: 2, screenX: 10, screenY: 10 });
        bar.fire('mousedown', { button: 1, screenX: 10, screenY: 10 });
        env.fireWindow('mousemove', { screenX: 300, screenY: 300 });

        assert.equal(env.posted.length, 0);
    });

    test(`${label} 事件：DOMContentLoaded 之后注册的 orielready 监听器能被触发`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        let announced = false;
        env.document.addEventListener('orielready', () => { announced = true; });
        fireDomEvent(env, 'DOMContentLoaded');
        assert.equal(announced, true, 'orielready 未派发到 DOMContentLoaded 之后注册的监听器');
    });

    test(`${label} 往返：invoke 产生协议消息并能收到回执`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const promise = env.window.oriel.invoke('x', { a: 1 });
        assert.deepEqual(env.posted.at(-1), {
            __oriel: 'invoke', id: 1, name: 'x', args: { a: 1 }, token: injectedToken,
        });
        deliverResult(env, 1, true, 42);
        assert.equal(await promise, 42);
    });

    test(`${label} 回执失败：ok:false 时 Promise reject 且消息透传`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const promise = env.window.oriel.invoke('x', {});
        deliverResult(env, 1, false, 'boom');
        await assert.rejects(promise, (error) => error.message === 'boom');
    });

    test(`${label} 参数校验：空 name 立即 reject`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        await assert.rejects(env.window.oriel.invoke('', {}), /name/);
    });

    test(`${label} 超时：回执永不返回时 Promise reject 并清理 pending`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        env.window.oriel.timeout = 10; // 缩短等待，验证 D-7 的超时清理路径
        const promise = env.window.oriel.invoke('never', {});
        await assert.rejects(promise, /超时/);
    });

    test(`${label} 事件：oriel.on 收到宿主推送，退订后不再触发`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const seen = [];
        const off = env.window.oriel.on('maximized', (v) => seen.push(v));

        deliverEvent(env, 'maximized', true);
        assert.deepEqual(seen, [true], '未收到事件');

        off();
        deliverEvent(env, 'maximized', false);
        assert.deepEqual(seen, [true], '退订后仍被调用');
    });

    test(`${label} 事件：单个处理器抛异常不影响其余处理器`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        const seen = [];
        env.window.oriel.on('x', () => { throw new Error('boom'); });
        env.window.oriel.on('x', () => seen.push(1));

        deliverEvent(env, 'x', null);
        assert.deepEqual(seen, [1], '一个处理器抛异常后其余处理器被跳过');
    });

    test(`${label} 事件：oriel.on 参数校验立即抛出`, () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        assert.throws(() => env.window.oriel.on('', () => { }), /name/);
        assert.throws(() => env.window.oriel.on('x', null), /handler/);
    });
}
