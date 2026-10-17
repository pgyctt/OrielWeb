// 全局形状：注入脚本装到页面上的 `window.oriel`。
//
// 单独声明而不是在 `core.js` 里到处 `/** @type {any} */` 断言：
// 断言一多，"这个字段到底存不存在"就没人知道了。声明之后，`window.oriel.label`
// 这类访问在**编译期**就会被检查——而 `label` 正是 P0-2 要加的东西，
// 早声明能让"还没实现"变成一个可见的类型问题，而不是运行期的 undefined。

import type { OrielTransport } from './types.js';

declare global {
    interface Window {
        /**
         * OrielWeb 的注入式传输层。
         *
         * 只在**可信来源**的文档里存在（见 docs/API.md §4）：远程页面里连这个对象都没有。
         * 页面代码请 import `@orielweb/api/*`，不要直接用这个对象。
         */
        oriel?: OrielTransport;
    }
}

export {};
