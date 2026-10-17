// 对标 `@tauri-apps/plugin-dialog`。
//
// **与 `window.Window.pickFile()` 的关系（一个刻意的决定）**：那条是既有的公开契约
// （只收标题与过滤器、返回单个路径），**冻结不动**；本模块是新入口，收 Tauri 形状的
// options（多选 / 过滤器 / 初始目录）并返回数组。两者共用底下的原生对话框，
// 所以不会出现"两份实现行为不一致"。
//
// 一处实现细节值得说：`message` 系列在 Tauri 那边返回**被点按钮的文案**，
// 而本库的命令层返回**语义**（`ok` / `cancel` / `yes` / `no`）。
// 原因是 Windows 的 `MessageBoxW` **只能显示系统文案**，若让命令返回"实际显示的文案"，
// 页面拿它与期望标签比对时会**静默得到错误答案**（用户点了"是"，显示的却是"确定"）。
// 语义与文案分开之后，文案只是这一层的事，判断不受影响。

import { invoke } from './core.js';

/**
 * `message` / `ask` / `confirm` 的选项。
 *
 * @typedef {object} DialogMessageOptions
 * @property {string} [title] 对话框标题。
 * @property {'info' | 'warning' | 'error'} [kind] 语气（决定图标）。
 * @property {string | {ok?: string, cancel?: string, yes?: string, no?: string}} [buttons]
 *   按钮集合：字符串（`'OkCancel'` / `'YesNo'` / `'YesNoCancel'` / `'Ok'`）或对象。
 * @property {string} [okLabel] `ask` / `confirm` 的肯定按钮文案。
 * @property {string} [cancelLabel] `ask` / `confirm` 的否定按钮文案。
 */

/**
 * 把"选项或标题字符串"归一成选项对象。
 *
 * @param {DialogMessageOptions | string | undefined} options
 * @returns {DialogMessageOptions}
 * @remarks Tauri 允许第二参直接给一个标题字符串，所以两种写法都要收。
 */
function normalizeOptions(options) {
    return typeof options === 'string' ? { title: options } : (options ?? {});
}

/**
 * 打开文件（或目录）选择对话框。
 *
 * @param {object} [options]
 * @param {string} [options.title] 对话框标题。
 * @param {string} [options.defaultPath] 初始目录，或一个完整路径（会拆出目录部分）。
 * @param {Array<{name: string, extensions: string[]}>} [options.filters] 文件类型过滤。
 *   扩展名带不带点都行；`['*']` 表示"所有文件"。
 * @param {boolean} [options.multiple] 允许多选（与 `directory` 不能同时为真）。
 * @param {boolean} [options.directory] 选目录而不是文件（**只支持单选**）。
 * @returns {Promise<string | string[] | null>} 多选时是数组；取消时是 `null`。
 * @throws {Error} 传了 `canCreateDirectories` / `recursive`（未实现，会明确报错）。
 * @remarks 形状与 Tauri 一致：单选给 `string`、多选给 `string[]`、取消给 `null`。
 */
export async function open(options = {}) {
    const selected = await invoke('dialog.open', { options });

    // 命令层统一返回数组（返回值形状稳定），这一层按"是不是多选"摊平——
    // 那样页面拿到的形状与 Tauri 一致，而命令层不需要页面去猜。
    if (!Array.isArray(selected) || selected.length === 0) {
        return null;
    }

    return options.multiple === true ? selected : selected[0];
}

/**
 * 保存文件对话框。
 *
 * @param {object} [options] 同 {@link open} 的 `title` / `defaultPath` / `filters`。
 * @returns {Promise<string | null>} 选中的路径；取消时是 `null`。
 */
export function save(options = {}) {
    return invoke('dialog.save', { options });
}

/**
 * 弹一个消息对话框。
 *
 * @param {string} message 正文。
 * @param {DialogMessageOptions | string} [options] 选项，或直接给标题字符串（Tauri 也接受）。
 * @returns {Promise<string | null>} 用户点的那个按钮的**文案**；被外部关掉时是 `null`。
 * @remarks
 * 返回值是**文案**（与 Tauri 一致）：所以 `ask` / `confirm` 能直接拿它跟
 * 自己期望的标签比对，调用方也能用它区分"是/否/取消"三个按钮。
 * 命令层返回的是语义，这里映射回文案。
 */
export function message(message, options) {
    return sendMessage(message, options);
}

/**
 * 询问"是/否"。
 *
 * @param {string} message
 * @param {DialogMessageOptions | string} [options] 同 {@link message}。
 * @returns {Promise<boolean>} 用户是否点了肯定性的那个按钮。
 */
export async function ask(message, options) {
    const opts = normalizeOptions(options);
    const okLabel = opts.okLabel ?? 'Yes';
    const cancelLabel = opts.cancelLabel ?? 'No';

    const clicked = await sendMessage(message, {
        title: opts.title,
        kind: opts.kind,
        buttons: { yes: okLabel, no: cancelLabel },
    });

    return clicked === okLabel;
}

/**
 * 询问"确定/取消"。
 *
 * @param {string} message
 * @param {DialogMessageOptions | string} [options] 同 {@link message}。
 * @returns {Promise<boolean>} 用户是否点了确定。
 */
export async function confirm(message, options) {
    const opts = normalizeOptions(options);
    const okLabel = opts.okLabel ?? 'Ok';
    const cancelLabel = opts.cancelLabel ?? 'Cancel';

    const clicked = await sendMessage(message, {
        title: opts.title,
        kind: opts.kind,
        buttons: { ok: okLabel, cancel: cancelLabel },
    });

    return clicked === okLabel;
}

/**
 * 三个导出函数共用的实现。
 *
 * @param {string} text
 * @param {DialogMessageOptions | string} [options]
 * @returns {Promise<string | null>}
 * @remarks
 * 抽成内部函数而不是让 `ask` / `confirm` 调导出的 `message`：
 * 那三个函数的**形参也叫 `message`**，而形参会遮蔽同名的导出函数——
 * 写成 `message(...)` 会在运行时得到"message is not a function"。
 * （Tauri 那边把它叫 `messageCommand` 正是躲这个。这里换个名字更直白。）
 * TypeScript 在 `checkJs` 下能抓到，但那是"碰巧被抓到"——形参改名就不会了，
 * 所以结构上避开它比依赖检查器更稳。
 */
async function sendMessage(text, options) {
    const opts = normalizeOptions(options);
    const { buttons, labels } = normalizeButtons(opts.buttons);

    const result = await invoke('dialog.message', {
        message: text,
        title: opts.title,
        kind: opts.kind,
        buttons,
    });

    return result === null || result === undefined ? null : labels[result] ?? null;
}

/**
 * 把 Tauri 的按钮写法归一成"命令层要的形状 + 语义 → 文案的映射"。
 *
 * @param {string | {ok?: string, cancel?: string, yes?: string, no?: string}} [buttons]
 * @returns {{buttons: string | Record<string, string>, labels: Record<string, string>}}
 * @remarks
 * Tauri 的写法有三种：省略、字符串（`'OkCancel'` 等）、对象（`{ok}` / `{ok,cancel}` /
 * `{yes,no,cancel}`）。这里统一成"命令层收字符串或对象"，同时给出
 * **语义 → 文案**的映射（命令层回来的是语义）。
 */
function normalizeButtons(buttons) {
    if (buttons === undefined || buttons === null) {
        return { buttons: 'Ok', labels: { ok: '确定', cancel: '取消', yes: '是', no: '否' } };
    }

    if (typeof buttons === 'string') {
        return { buttons, labels: { ok: '确定', cancel: '取消', yes: '是', no: '否' } };
    }

    const ok = buttons.ok ?? '确定';
    const cancel = buttons.cancel ?? '取消';
    const yes = buttons.yes ?? '是';
    const no = buttons.no ?? '否';

    if (buttons.yes !== undefined || buttons.no !== undefined) {
        return buttons.cancel !== undefined
            ? { buttons: { yes, no, cancel }, labels: { yes, no, cancel } }
            : { buttons: { yes, no }, labels: { yes, no } };
    }

    return buttons.cancel !== undefined
        ? { buttons: { ok, cancel }, labels: { ok, cancel } }
        : { buttons: { ok }, labels: { ok } };
}
