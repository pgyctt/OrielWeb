// OrielWeb 三平台共用的注入式桥接脚本模板（EmbeddedResource，见 OrielWeb.csproj）。
//
// 由 OrielBridgeTemplate 在创建窗口时替换这些占位符后注入：
//   __ORIEL_PLATFORM__ → 'windows' | 'macos' | 'linux'
//   __ORIEL_POST__     → 平台投递表达式（obj 为待发送对象，令牌已由 post 加上）
//   __ORIEL_CONSOLE_ENABLED__ → 'true' | 'false'（见 OrielWindowOptions.ConsoleForwarding）
//   __ORIEL_VERSION__  → 库版本（取程序集版本前三位，如 '0.1.2'）
//   __ORIEL_TOKEN__    → 本次进程运行的 IPC 令牌（32 位十六进制）
//   __ORIEL_TRUSTED__  → 可信 URL 前缀数组，如 ['https://app.oriel/']
//   __ORIEL_DOUBLE_CLICK_MS__  → 系统双击间隔（毫秒）
//   __ORIEL_DRAG_THRESHOLD_PX__ → 判定"这是拖动而不是点击"的位移阈值（逻辑像素）
//   __ORIEL_DRAG_SELECTOR__    → 宿主指定的拖动区域选择器（'' 表示只用属性标注）
//
// 三平台此前各自手抄一份，已导致缺陷同步传播（ready 的 TDZ、orielready 的时序
// 都曾三份全中）。任何修改都会同时作用于三平台——这正是合并的目的。
//
// 平台差异一览：
//   * 投递通道：Windows 为 chrome.webview.postMessage(对象)；
//     macOS/Linux 为 webkit.messageHandlers.oriel.postMessage(JSON 字符串)。
//   * 回执通道：Windows 经 chrome.webview 的 message 事件；
//     macOS/Linux 由宿主 evaluateJavaScript 调用 _onResult。
//   * ExecuteScript 回环：仅 macOS/Linux 使用 _evalScriptDone（宿主求值回传）；
//     Windows 走 COM 完成回调，该方法闲置但无害。

(() => {
    // 来源校验：不可信的文档**完全不安装** window.oriel——远程页面里连这个对象都不存在，
    // 而不是"装上再拦"。这样"远程页面不接 IPC"是结构性成立的，不必靠宿主侧再判一次。
    // __ORIEL_TRUSTED__ 是宿主注入的可信 URL 前缀数组（内嵌资源来源 + AllowOrigin 追加的）。
    const trusted = __ORIEL_TRUSTED__;
    const selfHref = location.href;
    if (!Array.isArray(trusted) || !trusted.some((prefix) => selfHref.indexOf(prefix) === 0)) {
        return;
    }

    if (window.__orielBridgeInstalled) return;
    window.__orielBridgeInstalled = true;

    const pending = new Map();
    const listeners = new Map();
    let seq = 0;

    // 每次进程启动生成一次，随脚本注入；每条出站消息都带上，宿主侧不匹配即丢弃。
    // 只安装到可信文档里，所以"令牌泄露给远程页面"这条路本来就不存在。
    const token = '__ORIEL_TOKEN__';

    const post = (obj) => {
        obj.token = token;
        __ORIEL_POST__;
    };

    // 先建 Promise、再建对象：Promise 构造器会同步执行 executor，
    // 若在对象字面量内引用 oriel，会落进暂时性死区（ReferenceError）
    // → ready 变成 rejected Promise 且 _resolveReady 永不赋值。
    let resolveReady;
    const ready = new Promise((resolve) => { resolveReady = resolve; });

    const oriel = {
        platform: __ORIEL_PLATFORM__,
        // 由宿主注入的库版本（见 OrielBridgeTemplate）。页面用它做能力探测，
        // 因此**不能**在这里写死字面量——写死会与包版本各自漂移，静默误导所有兼容判断。
        version: '__ORIEL_VERSION__',
        ready: ready,
        // 命令回执超时（毫秒），可在页面侧改写为其他值；<=0 表示不启用超时。
        // 没有它时，命令永不回执（如宿主侧异常导致 sink 未投递）会让 Promise
        // 永不 settle、pending 条目永驻，前端内存持续泄漏。
        timeout: 30000,
        invoke(name, args) {
            return new Promise((resolve, reject) => {
                if (typeof name !== 'string' || !name) {
                    reject(new Error('oriel.invoke: name 必须为非空字符串'));
                    return;
                }
                const id = ++seq;
                const timeoutMs = typeof oriel.timeout === 'number' && oriel.timeout > 0 ? oriel.timeout : 0;
                const timer = timeoutMs > 0
                    ? setTimeout(() => {
                        if (pending.delete(id)) {
                            reject(new Error('oriel.invoke 超时：命令 "' + name + '" 在 ' + timeoutMs + 'ms 内没有回执'));
                        }
                    }, timeoutMs)
                    : null;
                pending.set(id, { resolve: resolve, reject: reject, timer: timer });
                post({ __oriel: 'invoke', id: id, name: name, args: args === undefined ? null : args });
            });
        },
        _onResult(id, ok, payload) {
            const entry = pending.get(id);
            if (!entry) return;
            pending.delete(id);
            if (entry.timer !== null) clearTimeout(entry.timer);
            if (ok) entry.resolve(payload);
            else entry.reject(new Error(payload || ('oriel 命令失败 (id=' + id + ')')));
        },
        // 页面 → 宿主的单向消息（宿主侧 MessageReceived 事件）。与 invoke 的区别：
        // 不等回执、不占用回执 id——用于"通知宿主"这类不需要结果的语义。
        postMessage(name, payload) {
            if (typeof name !== 'string' || !name) {
                throw new Error('oriel.postMessage: name 必须为非空字符串');
            }
            post({ __oriel: 'message', name: name, payload: payload === undefined ? null : payload });
        },
        // 宿主 → 页面的事件通道。用于页面无法自行察觉的窗口状态变化，
        // 典型例子：用户在原生路径下最大化/还原（双击标题栏、拖边框到屏幕顶端、Win+↑）时
        // 刷新标题栏的"最大化/还原"图标。
        // 返回取消订阅函数。
        on(name, handler) {
            if (typeof name !== 'string' || !name || typeof handler !== 'function') {
                throw new Error('oriel.on: 需要 (name: string, handler: function)');
            }
            let set = listeners.get(name);
            if (!set) {
                set = new Set();
                listeners.set(name, set);
            }
            set.add(handler);
            return () => { set.delete(handler); };
        },
        _onEvent(name, value) {
            const set = listeners.get(name);
            if (!set) return;
            // 复制一份再遍历：处理器内部退订不会影响本次派发
            for (const handler of Array.from(set)) {
                try { handler(value); }
                catch (err) { console.error('oriel.on("' + name + '") 处理器抛出', err); }
            }
        },
        // 宿主 ExecuteScriptAsync 的页面回环（见 API.md：不使用 ObjC block /
        // GAsyncReadyCallback，改由页面回传完成 TCS）
        _evalScriptDone(id, json) {
            post({ __oriel: 'evalResult', id: id, json: json });
        }
    };

    window.oriel = oriel;

    // 页面 console → 宿主（宿主侧 ConsoleMessage 事件）。是否启用由注入时的
    // __ORIEL_CONSOLE_ENABLED__ 决定：未启用时代码保留但不执行，所以实现只存在于本模板，
    // 不需要在 C# 侧另抄一份（三份手抄脚本互相漂移正是这个模板要消灭的问题）。
    // 默认关闭的理由：包装 console 会改变页面对它的可观测行为（如 console.log.toString()），
    // 且高频输出会变成持续的 IPC 流量。
    if (__ORIEL_CONSOLE_ENABLED__) {
        const consoleLevels = ['log', 'info', 'warn', 'error', 'debug'];
        const renderConsoleArg = (value) => {
            try {
                if (typeof value === 'string') return value;
                if (value instanceof Error) return value.stack || String(value);
                if (typeof value === 'undefined') return 'undefined';
                return JSON.stringify(value);
            } catch (_) {
                return String(value);
            }
        };
        for (const level of consoleLevels) {
            const original = console[level];
            console[level] = function (...args) {
                try {
                    post({ __oriel: 'console', level: level, text: args.map(renderConsoleArg).join(' ') });
                } catch (_) {
                    // 投递失败不影响页面自身的 console 行为
                }
                return original.apply(console, args);
            };
        }
    }

    // 接收命令回执：{ __oriel:'result', id, ok, value | error }
    // 仅 Windows（chrome.webview）有 message 事件；macOS/Linux 由宿主调用 _onResult。
    if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
        window.chrome.webview.addEventListener('message', (e) => {
            const d = e.data;
            if (!d) return;
            if (d.__oriel === 'result') {
                oriel._onResult(d.id, d.ok === true, d.ok === true ? d.value : d.error);
            } else if (d.__oriel === 'event') {
                oriel._onEvent(d.name, d.value);
            }
        });
    }

    // ------------------------------------------------------------------------
    // 标题栏拖动与双击：由库接管，页面只需标注区域。
    //
    // 页面在自绘标题栏上写 data-oriel-drag-region（或由宿主指定选择器），此后：
    //   * 按下并移动超过阈值 → 移动窗口
    //   * 双击 → 最大化/还原
    // 区域内命中的交互元素（按钮、输入框……）与 data-oriel-no-drag 一律不接管。
    //
    // 为什么把这些放在库里：三平台的拖动形态差别很大（Windows 的原生拖动会吞掉
    // 后续点击、Wayland 下必须等指针动过阈值才能把移动交给合成器），每一个都曾在
    // 真机上以"双击没反应""窗口不跟手"的形式暴露过。让每个应用各写一遍等于让每个
    // 应用各踩一遍；而且"在 mousedown 里同步判断双击"必须知道系统双击间隔，
    // 那是宿主才知道的值。
    //
    // 三平台的发起方式：
    //   * Windows：win.drag 进入原生模态拖动（Windows 负责吸附/贴靠等系统行为）
    //   * macOS/Linux：win.dragStart → win.dragTo → win.dragEnd 的流式移动
    //   * Wayland 下 dragStart 只是"记下按下了"，宿主会等增量超过自己的阈值才交给合成器
    // ------------------------------------------------------------------------

    const doubleClickMs = __ORIEL_DOUBLE_CLICK_MS__;
    const dragThresholdPx = __ORIEL_DRAG_THRESHOLD_PX__;
    const hostDragSelector = __ORIEL_DRAG_SELECTOR__;
    const DRAG_ATTRIBUTE = 'data-oriel-drag-region';
    const NO_DRAG_ATTRIBUTE = 'data-oriel-no-drag';
    // 命中的这些元素不接管：标题栏上的按钮/输入框要能被正常点击
    const noDragSelector = 'button,input,select,textarea,a,[contenteditable],[' + NO_DRAG_ATTRIBUTE + ']';
    const isWindows = oriel.platform === 'windows';
    const DOUBLE_CLICK_SLOP_PX = 4;

    let lastPress = null;   // { t, x, y }：判断"这是不是双击的第二下"
    let session = null;     // 进行中的拖动

    const regionSelector = () =>
        hostDragSelector ? hostDragSelector + ',[' + DRAG_ATTRIBUTE + ']' : '[' + DRAG_ATTRIBUTE + ']';

    const inNoDragArea = (target) =>
        target instanceof Element && target.closest(noDragSelector) !== null;

    function onRegionMouseDown(e) {
        if (e.button !== 0 || inNoDragArea(e.target)) return;

        // 双击的第二下不进入拖动，让浏览器把 dblclick 正常派发出去。
        // 这里必须**同步**判定——不能 await 宿主：那次按下早已过去，
        // 而拖动一旦开始，第二次点击就到不了页面（Windows）或被合成器吞掉（Wayland）。
        const isSecondPress = e.detail >= 2
            || (lastPress !== null
                && (e.timeStamp - lastPress.t) <= doubleClickMs
                && Math.abs(e.screenX - lastPress.x) <= DOUBLE_CLICK_SLOP_PX
                && Math.abs(e.screenY - lastPress.y) <= DOUBLE_CLICK_SLOP_PX);
        lastPress = { t: e.timeStamp, x: e.screenX, y: e.screenY };
        if (isSecondPress) {
            endSession();
            return;
        }

        // 阻止默认行为（文本选择、拖拽图片）但不阻止后续事件
        e.preventDefault();

        // 上一次拖动若没正常收尾（松手落在窗口外，mouseup 到不了页面），残留的 window 监听
        // 会与这一次叠加，于是每个 mousemove 都发两次增量。先收干净再开始。
        endSession();

        session = {
            startX: e.screenX,
            startY: e.screenY,
            started: false,
            // dragStart 需要的窗口几何：只在真正开始时取一次
            geometry: null,
        };
        window.addEventListener('mousemove', onWindowMouseMove, true);
        window.addEventListener('mouseup', onWindowMouseUp, true);
    }

    function onWindowMouseMove(e) {
        if (session === null) return;
        const dx = e.screenX - session.startX;
        const dy = e.screenY - session.startY;

        if (!session.started) {
            if (Math.abs(dx) <= dragThresholdPx && Math.abs(dy) <= dragThresholdPx) return;
            session.started = true;

            if (isWindows) {
                // 原生模态拖动会一直阻塞到松开鼠标，此后不再需要页面事件
                endSession();
                oriel.invoke('win.drag').catch(reportDragFailure);
                return;
            }

            // macOS/Linux：把指针起点与窗口几何交给宿主，之后按增量移动。
            // 起点用**按下时**的位置而不是当前位置——否则窗口会在开始拖动的那一刻跳一下。
            session.geometry = {
                px: session.startX,
                py: session.startY,
                wx: window.screenX,
                wy: window.screenY,
                ww: window.outerWidth,
                wh: window.outerHeight,
                sh: window.screen.height,
            };
            oriel.invoke('win.dragStart', session.geometry).catch(reportDragFailure);
            return;
        }

        if (isWindows) return;
        oriel.invoke('win.dragTo', { dx: dx, dy: dy }).catch(reportDragFailure);
    }

    function onWindowMouseUp() {
        if (session === null) return;
        const wasStarted = session.started;
        endSession();
        if (wasStarted && !isWindows) {
            oriel.invoke('win.dragEnd').catch(reportDragFailure);
        }
    }

    function endSession() {
        session = null;
        window.removeEventListener('mousemove', onWindowMouseMove, true);
        window.removeEventListener('mouseup', onWindowMouseUp, true);
    }

    function reportDragFailure(error) {
        // 拖动失败不该静默：它表现为"窗口不动"，而原因（命令被能力配置拒掉之类）在别处看不见
        console.error('oriel: 窗口拖动失败', error);
    }

    function onRegionDoubleClick(e) {
        if (e.button !== 0 || inNoDragArea(e.target)) return;
        oriel.invoke('win.toggleMaximize').catch(reportDragFailure);
    }

    /** 把一个元素标成拖动区域（幂等：重复登记不会重复绑定）。 */
    function bindRegion(element) {
        if (!(element instanceof Element) || element.__orielDragBound === true) return;
        element.__orielDragBound = true;
        element.addEventListener('mousedown', onRegionMouseDown);
        element.addEventListener('dblclick', onRegionDoubleClick);
    }

    /**
     * 登记拖动区域：不传参数 = 重新扫描页面（适合 SPA 换掉标题栏之后）。
     * 返回这次扫描登记的元素个数。
     */
    oriel.dragRegion = function (target) {
        if (target === undefined || target === null) {
            const found = document.querySelectorAll(regionSelector());
            for (const element of found) bindRegion(element);
            return found.length;
        }

        if (typeof target === 'string') {
            const found = document.querySelectorAll(target);
            for (const element of found) bindRegion(element);
            return found.length;
        }

        if (target instanceof Element) {
            bindRegion(target);
            return 1;
        }

        throw new Error('oriel.dragRegion: 需要选择器、元素，或不传参数');
    };

    // ------------------------------------------------------------------------

    // 本脚本在 document 创建时注入（早于页面自身脚本），立即派发 orielready
    // 必然无人监听。改到 DOMContentLoaded——它晚于所有同步 / defer / type=module 脚本。
    // 注意：DOMContentLoaded 之后才动态注册的监听器仍收不到，因此文档主推
    // `await window.oriel.ready`，事件仅作为兼容性补充。
    const announceReady = () => document.dispatchEvent(new Event('orielready'));
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', announceReady, { once: true });
        // 拖动区域要等 DOM 就绪才能扫描（脚本注入早于页面自身的一切）
        document.addEventListener('DOMContentLoaded', () => { oriel.dragRegion(); }, { once: true });
    } else {
        queueMicrotask(announceReady);
        queueMicrotask(() => { oriel.dragRegion(); });
    }

    resolveReady();
})();
