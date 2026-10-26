// 文件拖放 + TauriEvent 的拖放名字。
//
// 本库走的是 OS 级投放（Host 的 FileDropped），不是 HTML5 拖放：
// 页面不需要 preventDefault，路径由宿主交过来。

import { TauriEvent } from "../vendor/orielweb-api/event.js";
import { getCurrentWindow } from "../vendor/orielweb-api/window.js";
import { log } from "../ui.js";

const win = getCurrentWindow();
let off = null;
let zone = null;

function mark(state) {
    if (zone === null) {
        return;
    }
    zone.classList.toggle("over", state === "enter" || state === "over");
    zone.dataset.state = state;
}

export default {
    id: "dragdrop",
    title: "DragDrop 文件拖放",
    module: "Window.onDragDropEvent",
    blurb:
        "把文件从资源管理器拖进窗口。四个阶段都有事件，但只有 drop 一定带 paths；"
        + "位置是客户区物理像素，Linux 的 enter 拿不到路径（GTK 的限制）。",
    groups: [
        {
            title: "OS 级投放",
            items: [
                {
                    node: () =>
                        (zone = (() => {
                            const element = document.createElement("div");
                            element.className = "drop-zone";
                            element.textContent = "把文件拖到这里（先点下面的「订阅」）";
                            return element;
                        })()),
                },
                {
                    name: "onDragDropEvent(handler)",
                    run: async () => {
                        if (off !== null) {
                            return "已订阅";
                        }
                        off = await win.onDragDropEvent((event) => {
                            mark(event.type);
                            const paths = event.payload?.paths ?? [];
                            log(`drag ${event.type}${paths.length ? ` → ${paths.join(", ")}` : ""}`, "info");
                        });
                        return "已订阅：enter / over / drop / leave 都会进日志";
                    },
                },
                {
                    name: "退订",
                    run: () => {
                        if (off === null) {
                            return "没有订阅";
                        }
                        off();
                        off = null;
                        mark("leave");
                        return "已退订";
                    },
                },
            ],
        },
        {
            title: "TauriEvent 拖放常量",
            items: [
                {
                    name: "TauriEvent.DRAG_ENTER / OVER / DROP / LEAVE",
                    run: () => ({
                        enter: TauriEvent.DRAG_ENTER,
                        over: TauriEvent.DRAG_OVER,
                        drop: TauriEvent.DRAG_DROP,
                        leave: TauriEvent.DRAG_LEAVE,
                    }),
                },
            ],
        },
    ],
};
