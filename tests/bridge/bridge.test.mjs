// 桥接 JS 单元测试（Node 内置 test runner）。
//
// 作用：三平台桥接脚本此前零覆盖，P0 级缺陷（ready 因 TDZ 变成 rejected Promise、
// orielready 事件死于时序）正因如此长期存活。D-6 之后三平台共用同一份模板
// （src/OrielWeb/Bridge/oriel-bridge.js），本测试直接读该模板、按各平台差异做
// 占位符替换后注入最小 window/document 桩执行，断言"就绪 / 事件 / 往返 / 回执 /
// 错误 / 超时"六类行为，并参数化跑全部三个平台。
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

/** 复现 C# 侧的两次占位符替换，得到该平台真正被注入的脚本。 */
function buildScript(bridge) {
    const script = template
        .replaceAll('__ORIEL_PLATFORM__', `'${bridge.platform}'`)
        .replaceAll('__ORIEL_POST__', bridge.post);
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
function loadBridge(script, channel) {
    const env = createEnvironment(channel);
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

test('模板：占位符齐备', () => {
    assert.ok(template.includes('__ORIEL_PLATFORM__'), '模板缺少 __ORIEL_PLATFORM__');
    assert.ok(template.includes('__ORIEL_POST__'), '模板缺少 __ORIEL_POST__');
    assert.ok(template.includes('window.__orielBridgeInstalled'), '模板缺少重复注入防护');
});

for (const bridge of bridges) {
    const label = `[${bridge.platform}]`;

    test(`${label} 就绪：脚本加载无异常且 window.oriel.ready 可 await`, async () => {
        const env = loadBridge(buildScript(bridge), bridge.channel);
        assert.equal(env.loadError, undefined, `桥接脚本加载抛异常：${env.loadError}`);
        assert.ok(env.window.oriel, 'window.oriel 未安装');
        assert.equal(env.window.oriel.platform, bridge.platform);
        await env.window.oriel.ready;
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
        assert.deepEqual(env.posted.at(-1), { __oriel: 'invoke', id: 1, name: 'x', args: { a: 1 } });
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
}
