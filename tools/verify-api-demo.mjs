// ApiDemo 的无人点击验证：通过 WebView2 的 CDP 端口把每个面板的按钮按一遍。
//
// 用法（Windows；需要先让 ApiDemo 带调试端口启动）：
//
//   WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9333" \
//     samples/ApiDemo/bin/Debug/net10.0/ApiDemo.exe
//
//   node tools/verify-api-demo.mjs --panes core,path,dpi,image,event,fs,window
//   node tools/verify-api-demo.mjs --panes app --skip "exit|hide"
//
// 它只点"点一下就该有回执"的项；会弹模态、拉起外部程序、改系统状态的面板不要放进来
// （dialog / clipboard / opener / notification / autostart / menu 的 popup / dragdrop）。
//
// 判定：标了「预期失败」的项、以及 code=not_supported 的项不算回归；
// 其余红色回执会让退出码为 1。`code=internal` 也算失败——它可能是环境限制，
// 但那种情况应当由人判一次，而不是让脚本替它开脱。

import process from "node:process";

const options = parseArgs(process.argv.slice(2));
const port = options.port ?? "9333";
const panes = (options.panes ?? "core,path,dpi,image,event,fs,window").split(",");
const skip = options.skip ? new RegExp(options.skip) : null;

const page = await findTarget(port);
const ws = new WebSocket(page.webSocketDebuggerUrl);

await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = reject;
});

let nextId = 0;
const pending = new Map();
ws.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
        pending.get(message.id)(message);
        pending.delete(message.id);
    }
};

function send(method, params) {
    const id = ++nextId;
    ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve) => pending.set(id, resolve));
}

async function evaluate(expression) {
    const reply = await send("Runtime.evaluate", {
        expression,
        awaitPromise: true,
        returnByValue: true,
    });
    if (reply.result?.exceptionDetails) {
        throw new Error(JSON.stringify(reply.result.exceptionDetails));
    }
    return reply.result?.result?.value;
}

const results = [];

for (const pane of panes) {
    await evaluate(`location.hash = '#/${pane}'`);
    await sleep(400);

    const actions = await evaluate(`Array.from(document.querySelectorAll('.action')).map((row, index) => ({
        index,
        name: row.querySelector('.action-name code')?.textContent ?? '?',
        badge: row.querySelector('.badge')?.textContent ?? '',
        hasButton: Boolean(row.querySelector('button.run')),
    }))`);

    if (actions.length === 0) {
        results.push({ pane, name: "(面板没有动作)", cls: "missing" });
        continue;
    }

    for (const action of actions) {
        if (!action.hasButton) {
            continue;
        }

        if (isSkipped(action.name)) {
            results.push({ pane, name: action.name, skipped: true });
            continue;
        }

        const outcome = await evaluate(`(async () => {
            const row = document.querySelectorAll('.action')[${action.index}];
            const button = row.querySelector('button.run');
            const out = row.querySelector('.result');
            button.click();
            const started = Date.now();
            while (Date.now() - started < 9000) {
                if (!button.disabled && out && out.textContent && !out.classList.contains('pending')) {
                    return { cls: out.className, text: out.textContent };
                }
                await new Promise((resolve) => setTimeout(resolve, 80));
            }
            return { cls: 'timeout', text: out?.textContent ?? '' };
        })()`);

        results.push({ pane, name: action.name, badge: action.badge, ...outcome });
        await sleep(120);
    }
}

ws.close();

const unexpected = results.filter((item) => isUnexpected(item));
const timeouts = results.filter((item) => item.cls === "timeout");
const missing = results.filter((item) => item.cls === "missing");
const skipped = results.filter((item) => item.skipped);

for (const item of results.filter((entry) => entry.cls?.includes("err"))) {
    const label = item.badge.includes("预期失败") ? "预期" : "失败";
    console.log(`[${label}][${item.pane}] ${item.name}\n    ${String(item.text).replace(/\n/g, "\n    ")}`);
}

console.log(
    `\n共 ${results.length} 条：意外失败 ${unexpected.length}，超时 ${timeouts.length}，`
    + `空面板 ${missing.length}，跳过 ${skipped.length}。`,
);

if (unexpected.length > 0 || timeouts.length > 0 || missing.length > 0) {
    process.exitCode = 1;
}

function isSkipped(name) {
    if (/^(close|destroy)\(\)/.test(name)) {
        return true;
    }
    if (/start(Dragging|ResizeDragging)/.test(name)) {
        return true;
    }
    return skip !== null && skip.test(name);
}

function isUnexpected(item) {
    if (item.cls === "timeout" || item.cls === "missing") {
        return true;
    }
    if (!item.cls?.includes("err")) {
        return false;
    }
    if (item.badge.includes("预期失败")) {
        return false;
    }
    return !String(item.text).includes("[code=not_supported]");
}

function parseArgs(argv) {
    const result = {};
    for (let i = 0; i < argv.length; i += 1) {
        const key = argv[i];
        if (!key.startsWith("--")) {
            continue;
        }
        result[key.slice(2)] = argv[i + 1];
        i += 1;
    }
    return result;
}

async function findTarget(cdpPort) {
    for (let attempt = 0; attempt < 40; attempt += 1) {
        try {
            const list = await (await fetch(`http://127.0.0.1:${cdpPort}/json`)).json();
            const target = list.find((item) => item.type === "page" && item.url.includes("oriel://"));
            if (target) {
                return target;
            }
        } catch {
            // 端口还没起来；继续等
        }
        await sleep(250);
    }

    throw new Error(
        `找不到 CDP target（端口 ${cdpPort}）。先带调试端口启动 ApiDemo：\n`
        + `  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=${cdpPort}" ApiDemo.exe`,
    );
}

function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
}
