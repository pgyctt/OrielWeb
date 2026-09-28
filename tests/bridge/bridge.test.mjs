// 桥接 JS 单元测试（Node 内置 test runner）。
//
// 作用：三平台桥接脚本（Windows/macOS/Linux）此前零覆盖，P0 级缺陷
// （ready 因 TDZ 变成 rejected Promise、orielready 事件死于时序）正因如此长期存活。
// 本测试直接从 C# 原始字符串字面量提取 JS，注入最小 window/document 桩后执行，
// 断言"就绪 / 事件 / 往返 / 回执 / 错误"五类行为，并参数化跑全部三份桥接——
// 三份代码历史上多次出现缺陷同步传播，必须逐一锁死。
//
// 运行：node --test tests/bridge/

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../../', import.meta.url));

/** 三份桥接的 C# 载体文件。channel 用于区分消息通道。 */
const bridges = [
    { platform: 'windows', cs: 'src/OrielWeb/Bridge/OrielBridgeJs.cs', channel: 'chrome' },
    { platform: 'macos', cs: 'src/OrielWeb/Platform/MacOS/MacOSBridgeJs.cs', channel: 'webkit' },
    { platform: 'linux', cs: 'src/OrielWeb/Platform/Linux/LinuxBridgeJs.cs', channel: 'webkit' },
];

/**
 * 从 C# 原始字符串字面量（""" ... """）中提取注入的 JS。
 * 若某处未使用 `const string Script = """` 形态，这里会显式失败而不是静默跳过。
 */
function extractScript(csRelativePath) {
    const source = readFileSync(join(repoRoot, csRelativePath), 'utf8');
    const match = source.match(/const string Script = """\r?\n([\s\S]*?)\r?\n\s*""";/);
    assert.ok(match, `未能从 ${csRelativePath} 提取 Script 原始字符串`);
    return match[1];
}

/** 最小环境桩：只提供桥接脚本实际用到的 API。 */
function createEnvironment() {
    const posted = [];
    const messageListeners = [];
    const domListeners = new Map();

    const windowStub = {
        chrome: {
            webview: {
                postMessage: (message) => posted.push(message),
                addEventListener: (type, listener) => {
                    if (type === 'message') messageListeners.push(listener);
                },
            },
        },
        webkit: {
            messageHandlers: {
                // macOS/Linux 版以 JSON 字符串投递，桩内还原为对象，便于统一断言
                oriel: { postMessage: (json) => posted.push(JSON.parse(json)) },
            },
        },
    };

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
    };

    return { window: windowStub, document: documentStub, posted, messageListeners, domListeners };
}

/** 加载桥接脚本。加载期的异常不抛出而是记录，便于单独断言"加载本身无异常"。 */
function loadBridge(script) {
    const env = createEnvironment();
    const EventStub = class { constructor(type) { this.type = type; } };
    try {
        new Function('window', 'document', 'Event', script)(env.window, env.document, EventStub);
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

for (const bridge of bridges) {
    const label = `[${bridge.platform}]`;

    test(`${label} 就绪：脚本加载无异常且 window.oriel.ready 可 await`, async () => {
        const env = loadBridge(extractScript(bridge.cs));
        assert.equal(env.loadError, undefined, `桥接脚本加载抛异常：${env.loadError}`);
        assert.ok(env.window.oriel, 'window.oriel 未安装');
        await env.window.oriel.ready;
    });

    test(`${label} 事件：DOMContentLoaded 之后注册的 orielready 监听器能被触发`, () => {
        const env = loadBridge(extractScript(bridge.cs));
        let announced = false;
        env.document.addEventListener('orielready', () => { announced = true; });
        fireDomEvent(env, 'DOMContentLoaded');
        assert.equal(announced, true, 'orielready 未派发到 DOMContentLoaded 之后注册的监听器');
    });

    test(`${label} 往返：invoke 产生协议消息并能收到回执`, async () => {
        const env = loadBridge(extractScript(bridge.cs));
        const promise = env.window.oriel.invoke('x', { a: 1 });
        assert.deepEqual(env.posted.at(-1), { __oriel: 'invoke', id: 1, name: 'x', args: { a: 1 } });
        deliverResult(env, 1, true, 42);
        assert.equal(await promise, 42);
    });

    test(`${label} 回执失败：ok:false 时 Promise reject 且消息透传`, async () => {
        const env = loadBridge(extractScript(bridge.cs));
        const promise = env.window.oriel.invoke('x', {});
        deliverResult(env, 1, false, 'boom');
        await assert.rejects(promise, (error) => error.message === 'boom');
    });

    test(`${label} 参数校验：空 name 立即 reject`, async () => {
        const env = loadBridge(extractScript(bridge.cs));
        await assert.rejects(env.window.oriel.invoke('', {}), /name/);
    });
}
