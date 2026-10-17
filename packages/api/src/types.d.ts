// 传输层形状：注入脚本（src/OrielWeb/Bridge/oriel-bridge.js）暴露的 `window.oriel`。
//
// 这是**内部契约**，不是公开 API。页面应当 import `@orielweb/api/*`，而不是直接用这个对象。
// 单独写出来是为了让 `internal.js` 与 `mocks.js` 有个可校验的形状——
// 没有它，`setTransportOverride` 接受任何东西，装错 mock 要到运行时才发现。
//
// 文件名带 `.d.ts` 后缀，但 JSDoc 里按 ESM 惯例引用为 `./types.js`：
// TypeScript 会把 `.js` 解析到同目录的 `.d.ts`，这样打包器与编辑器都能正确跟进。

/** OrielWeb 的注入式传输层（`window.oriel`）。 */
export interface OrielTransport {
    /**
     * 调用宿主命令。
     *
     * @param name 命令名。
     * @param args 参数对象；省略时宿主收到 `null`。
     * @returns 命令返回值；失败时 reject 一个 `Error`（message 是宿主给的文案）。
     */
    invoke(name: string, args?: Record<string, unknown> | null): Promise<unknown>;

    /**
     * 是否支持 `invoke(name, args, { timeout })` 三参形态（P1-7 之后为 `true`）。
     *
     * 这是个**显式声明**而不是靠 `invoke.length` 探测：探测在包装/压缩后会给出错误答案，
     * 而错误答案的表现是"设了超时却没生效"。
     */
    supportsPerCallTimeout?: boolean;

    /** 宿主平台：`windows` / `macos` / `linux`。 */
    platform?: string;

    /** 库版本（`Major.Minor.Build`），由宿主注入。注意这是**库**版本，不是应用版本。 */
    version?: string;

    /** 本窗口的 label（P0-2 之后可用）。 */
    label?: string;

    /** 桥接脚本就绪的 Promise。 */
    ready?: Promise<void>;
}
