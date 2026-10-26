// `@orielweb/api/autostart`：开机自启的查询与开关。
//
// 会真的往注册表 / LaunchAgent / XDG autostart 里写东西——这是验证台该做的，
// 但每一项都标清楚副作用。

import { disable, enable, isEnabled } from "../vendor/orielweb-api/autostart.js";

export default {
    id: "autostart",
    title: "Autostart 开机自启",
    module: "@orielweb/api/autostart",
    blurb: "改的是系统状态。开启后本示例会随系统启动——验证完记得关掉。",
    groups: [
        {
            title: "状态",
            items: [
                { name: "isEnabled()", run: () => isEnabled() },
                { name: "enable()", badge: "改系统状态", run: () => enable() },
                { name: "disable()", run: () => disable() },
                {
                    name: "isEnabled() → enable() → isEnabled() → disable()",
                    note: "一条动作跑完整往返，回执里给三步结果。",
                    run: async () => {
                        const before = await isEnabled();
                        const enabled = await enable();
                        const after = await isEnabled();
                        await disable();
                        return { before, enableReturned: enabled, after, restored: await isEnabled() };
                    },
                },
            ],
        },
    ],
};
