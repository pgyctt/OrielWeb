/**
 * 应用显示名（对标 Tauri 的 `tauri.conf.json > productName`）。
 *
 * @returns {Promise<string>}
 * @remarks **无需能力声明**（只读元信息，整组放行）。下同。
 */
export function getName(): Promise<string>;
/**
 * 应用版本（对标 Tauri 的 `tauri.conf.json > version`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 默认取自入口程序集的版本信息，所以它跟随 csproj 的 `<Version>`；
 * 显式配置见宿主侧的 `WithAppInfo`。**构建元数据（`+` 之后那一段）会被去掉**——
 * 那里面通常是 git 提交号，参与版本比较时会让同一个发布版本看起来"不相等"。
 */
export function getVersion(): Promise<string>;
/**
 * 应用标识符，形如 `com.example.app`（对标 Tauri 的 `tauri.conf.json > identifier`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 它是**应用数据目录的组成部分**（见文档里的"应用数据目录"一节）。
 * 没显式配置时从程序集名推断，正式应用应当显式给——等数据已经写进去之后再改就要做迁移。
 */
export function getIdentifier(): Promise<string>;
/**
 * 库版本（对标 Tauri 的 `getTauriVersion()`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * **语义提醒**：Tauri 的这个函数返回**框架**版本（`2.x.y`），本库返回**库**版本
 * （`0.4.0` 这一类）。数字放在一起比较没有意义——迁移时如果代码里有
 * `getTauriVersion()` 的版本号判断，那一段必须重写，不能照搬。
 *
 * 与 `window.oriel.version` 同源（都来自程序集版本），所以两处做能力探测不会得到矛盾答案。
 */
export function getTauriVersion(): Promise<string>;
/**
 * 宿主是否支持多窗口。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 三平台都支持，所以**恒为 `true`**。保留它是因为 Tauri 上它是个真查询
 * （移动端返回 `false`），迁移代码里常有 `if (await supportsMultipleWindows())` 这类分支；
 * 删掉会让那些代码编译不过。没有谎报：桌面端本来就是 true。
 */
export function supportsMultipleWindows(): Promise<boolean>;
