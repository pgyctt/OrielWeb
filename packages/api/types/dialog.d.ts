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
export function open(options?: {
    title?: string | undefined;
    defaultPath?: string | undefined;
    filters?: {
        name: string;
        extensions: string[];
    }[] | undefined;
    multiple?: boolean | undefined;
    directory?: boolean | undefined;
}): Promise<string | string[] | null>;
/**
 * 保存文件对话框。
 *
 * @param {object} [options] 同 {@link open} 的 `title` / `defaultPath` / `filters`。
 * @returns {Promise<string | null>} 选中的路径；取消时是 `null`。
 */
export function save(options?: object): Promise<string | null>;
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
export function message(message: string, options?: DialogMessageOptions | string): Promise<string | null>;
/**
 * 询问"是/否"。
 *
 * @param {string} message
 * @param {DialogMessageOptions | string} [options] 同 {@link message}。
 * @returns {Promise<boolean>} 用户是否点了肯定性的那个按钮。
 */
export function ask(message: string, options?: DialogMessageOptions | string): Promise<boolean>;
/**
 * 询问"确定/取消"。
 *
 * @param {string} message
 * @param {DialogMessageOptions | string} [options] 同 {@link message}。
 * @returns {Promise<boolean>} 用户是否点了确定。
 */
export function confirm(message: string, options?: DialogMessageOptions | string): Promise<boolean>;
/**
 * `message` / `ask` / `confirm` 的选项。
 */
export type DialogMessageOptions = {
    /**
     * 对话框标题。
     */
    title?: string | undefined;
    /**
     * 语气（决定图标）。
     */
    kind?: "info" | "warning" | "error" | undefined;
    /**
     * 按钮集合：字符串（`'OkCancel'` / `'YesNo'` / `'YesNoCancel'` / `'Ok'`）或对象。
     */
    buttons?: string | {
        ok?: string;
        cancel?: string;
        yes?: string;
        no?: string;
    } | undefined;
    /**
     * `ask` / `confirm` 的肯定按钮文案。
     */
    okLabel?: string | undefined;
    /**
     * `ask` / `confirm` 的否定按钮文案。
     */
    cancelLabel?: string | undefined;
};
