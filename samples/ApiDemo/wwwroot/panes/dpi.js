// `@orielweb/api/dpi`：尺寸/位置的四种类型与换算。
//
// 纯 JS，不发 IPC——唯一的宿主事实是 scaleFactor()。

import {
    LogicalPosition,
    LogicalSize,
    PhysicalPosition,
    PhysicalSize,
    logicalToPhysical,
    normalizePosition,
    normalizeSize,
    physicalToLogical,
} from "../vendor/orielweb-api/dpi.js";
import { getCurrentWindow } from "../vendor/orielweb-api/window.js";

const win = getCurrentWindow();

export default {
    id: "dpi",
    title: "DPI 尺寸与位置",
    module: "@orielweb/api/dpi",
    blurb:
        "Logical / Physical 两套坐标。normalize* 接受对象、实例、数字对三种形态；"
        + "换算用窗口真实的 scaleFactor，不是写死的 1。",
    groups: [
        {
            title: "尺寸",
            items: [
                { name: "new LogicalSize(800, 600)", run: () => new LogicalSize(800, 600) },
                { name: "new PhysicalSize(1600, 1200)", run: () => new PhysicalSize(1600, 1200) },
                {
                    name: "normalizeSize([800, 600])",
                    note: "缺 type 时按 Logical 算。",
                    run: () => normalizeSize([800, 600]),
                },
                {
                    name: "normalizeSize({ width, height }, 'Physical')",
                    run: () => normalizeSize({ width: 1600, height: 1200 }, "Physical"),
                },
                {
                    name: "normalizeSize(new LogicalSize(...))",
                    run: () => normalizeSize(new LogicalSize(1024, 768)),
                },
            ],
        },
        {
            title: "位置",
            items: [
                { name: "new LogicalPosition(10, 20)", run: () => new LogicalPosition(10, 20) },
                { name: "new PhysicalPosition(100, 200)", run: () => new PhysicalPosition(100, 200) },
                { name: "normalizePosition({ x, y })", run: () => normalizePosition({ x: 30, y: 40 }) },
                { name: "normalizePosition([30, 40], 'Physical')", run: () => normalizePosition([30, 40], "Physical") },
            ],
        },
        {
            title: "换算",
            items: [
                {
                    name: "logicalToPhysical(800, scaleFactor)",
                    run: async () => {
                        const scale = await win.scaleFactor();
                        return { scaleFactor: scale, physical: logicalToPhysical(800, scale) };
                    },
                },
                {
                    name: "physicalToLogical(1600, scaleFactor)",
                    run: async () => {
                        const scale = await win.scaleFactor();
                        return { scaleFactor: scale, logical: physicalToLogical(1600, scale) };
                    },
                },
                {
                    name: "往返一致？",
                    run: async () => {
                        const scale = await win.scaleFactor();
                        const physical = logicalToPhysical(800, scale);
                        return {
                            logical: 800,
                            physical,
                            back: physicalToLogical(physical, scale),
                        };
                    },
                },
                {
                    name: "window.innerSize() vs outerSize()",
                    note: "同一个窗口的两套尺寸；差值就是装饰。",
                    run: async () => ({
                        inner: await win.innerSize(),
                        outer: await win.outerSize(),
                    }),
                },
            ],
        },
    ],
};
