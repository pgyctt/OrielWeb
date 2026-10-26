// `@orielweb/api/fs`：文本/二进制/目录/句柄/监视。
//
// 所有路径都落在 `$APPDATA/sandbox`（能力配置里的 scope 就是 $APPDATA/**）。
// 最后一条故意读 scope 外的文件——它应当以 path_not_allowed 失败，
// 那条回执同时证明了"命令授权"与"路径 scope"是两道独立的关。

import {
    SeekMode,
    copyFile,
    create,
    exists,
    lstat,
    mkdir,
    open,
    readDir,
    readFile,
    readTextFile,
    readTextFileLines,
    remove,
    rename,
    size,
    stat,
    truncate,
    watch,
    watchImmediate,
    writeFile,
    writeTextFile,
} from "../vendor/orielweb-api/fs.js";
import { BaseDirectory } from "../vendor/orielweb-api/path.js";
import { join } from "../vendor/orielweb-api/path.js";
import { log } from "../ui.js";
import { sandbox, tinyPng } from "./common.js";

let handle = null;
let unwatch = null;

async function inSandbox(name) {
    return join(await sandbox(), name);
}

export default {
    id: "fs",
    title: "FS 文件系统",
    module: "@orielweb/api/fs",
    blurb:
        "文本、二进制、目录、句柄与监视。全部限制在 $APPDATA/sandbox 内；"
        + "最后一条越界读取是故意的，用来验证 path_not_allowed。",
    groups: [
        {
            title: "文本",
            items: [
                {
                    name: "writeTextFile('note.txt', text)",
                    note: "父目录会自动创建；不写 BOM。",
                    inputs: [{ key: "text", label: "text", value: "第一行\n第二行\n", width: 180 }],
                    run: async (v) => {
                        const path = await inSandbox("note.txt");
                        await writeTextFile(path, v.text ?? "");
                        return path;
                    },
                },
                {
                    name: "readTextFile('note.txt')",
                    run: async () => readTextFile(await inSandbox("note.txt")),
                },
                {
                    name: "readTextFileLines('note.txt')",
                    note: "异步迭代器；不流式（宿主整文件读入）。",
                    run: async () => {
                        const lines = [];
                        for await (const line of await readTextFileLines(await inSandbox("note.txt"))) {
                            lines.push(line);
                        }
                        return lines;
                    },
                },
                {
                    name: "writeTextFile + baseDir: AppData",
                    note: "相对路径按 BaseDirectory 解析（绝对路径不参与）。",
                    run: async () => {
                        await writeTextFile("api-demo-base-dir.txt", "written via baseDir", { baseDir: BaseDirectory.AppData });
                        return readTextFile("api-demo-base-dir.txt", { baseDir: BaseDirectory.AppData });
                    },
                },
            ],
        },
        {
            title: "元信息与目录",
            items: [
                { name: "exists('note.txt')", run: async () => exists(await inSandbox("note.txt")) },
                { name: "size('note.txt')", run: async () => size(await inSandbox("note.txt")) },
                { name: "stat('note.txt')", run: async () => stat(await inSandbox("note.txt")) },
                { name: "lstat('note.txt')", run: async () => lstat(await inSandbox("note.txt")) },
                {
                    name: "readDir(sandbox)",
                    run: async () => readDir(await sandbox()),
                },
                {
                    name: "mkdir('nested/deep', { recursive: true })",
                    run: async () => {
                        const path = await inSandbox("nested/deep");
                        await mkdir(path, { recursive: true });
                        return path;
                    },
                },
                {
                    name: "rename('note.txt', 'renamed.txt')",
                    note: "重复执行是安全的：目标存在时先删掉，否则第二次会报 AlreadyExists。",
                    run: async () => {
                        const from = await inSandbox("note.txt");
                        const to = await inSandbox("renamed.txt");
                        await writeTextFile(from, "第一行\n第二行\n");
                        if (await exists(to)) {
                            await remove(to);
                        }
                        await rename(from, to);
                        return "已重命名";
                    },
                },
                {
                    name: "copyFile('renamed.txt', 'copy.txt')",
                    run: async () => {
                        await copyFile(await inSandbox("renamed.txt"), await inSandbox("copy.txt"));
                        return "已复制";
                    },
                },
                {
                    name: "truncate('copy.txt', 4)",
                    run: async () => {
                        await truncate(await inSandbox("copy.txt"), 4);
                        return readTextFile(await inSandbox("copy.txt"));
                    },
                },
                {
                    name: "remove('copy.txt')",
                    run: async () => {
                        await remove(await inSandbox("copy.txt"));
                        return "已删除";
                    },
                },
                {
                    name: "remove('nested', { recursive: true })",
                    run: async () => {
                        await remove(await inSandbox("nested"), { recursive: true });
                        return "已删除目录";
                    },
                },
            ],
        },
        {
            title: "二进制",
            items: [
                {
                    name: "writeFile('blob.bin', pngBytes)",
                    run: async () => {
                        const path = await inSandbox("blob.bin");
                        await writeFile(path, tinyPng());
                        return `${path}（${tinyPng().length} 字节）`;
                    },
                },
                {
                    name: "readFile('blob.bin')",
                    run: async () => {
                        const bytes = await readFile(await inSandbox("blob.bin"));
                        return { length: bytes.length, head: bytes.slice(0, 8) };
                    },
                },
            ],
        },
        {
            title: "句柄（open / FileHandle）",
            note: "scope 只在 open/create 时判；拿到句柄后的读写不再看路径。",
            items: [
                {
                    name: "create('created.bin')",
                    note: "创建（已存在则清空）并打开句柄；这里读完大小就关掉。",
                    run: async () => {
                        const created = await create(await inSandbox("created.bin"));
                        const info = await created.stat();
                        await created.close();
                        return `已创建，${info.size} 字节`;
                    },
                },
                {
                    name: "open('blob.bin', { read, write })",
                    run: async () => {
                        handle = await open(await inSandbox("blob.bin"), { read: true, write: true });
                        const info = await handle.stat();
                        return `已打开：${info.size} 字节`;
                    },
                },
                {
                    name: "handle.seek(0, SeekMode.End)",
                    run: () => requireHandle((h) => h.seek(0, SeekMode.End)),
                },
                {
                    name: "handle.write([1, 2, 3])",
                    run: () => requireHandle((h) => h.write(new Uint8Array([1, 2, 3]))),
                },
                {
                    name: "handle.stat()",
                    run: () => requireHandle((h) => h.stat()),
                },
                {
                    name: "handle.truncate()",
                    note: "省略长度 = 截到当前位置。",
                    run: () => requireHandle((h) => h.truncate()),
                },
                {
                    name: "handle.seek(0, Start) + read(16)",
                    run: () => requireHandle(async (h) => {
                        await h.seek(0, SeekMode.Start);
                        const buffer = new Uint8Array(16);
                        const read = await h.read(buffer);
                        return { read, bytes: buffer.slice(0, read ?? 0) };
                    }),
                },
                {
                    name: "handle.close()",
                    run: async () => {
                        if (handle === null) {
                            return "没有打开的句柄";
                        }
                        await handle.close();
                        handle = null;
                        return "已关闭";
                    },
                },
            ],
        },
        {
            title: "监视",
            note: "事件只回给发起监视的窗口；改文件时看日志。",
            items: [
                {
                    name: "watch(sandbox, cb, { recursive: true })",
                    run: async () => {
                        if (unwatch !== null) {
                            return "已经在监视（先 unwatch）";
                        }
                        const dir = await sandbox();
                        unwatch = await watch(dir, (event) => {
                            log(`watch → ${JSON.stringify(event.type)} ${event.paths.join(", ")}`, "info");
                        }, { recursive: true, delayMs: 300 });
                        return `正在监视 ${dir}`;
                    },
                },
                {
                    name: "watchImmediate(sandbox, cb)",
                    run: async () => {
                        if (unwatch !== null) {
                            return "已经在监视（先 unwatch）";
                        }
                        const dir = await sandbox();
                        unwatch = await watchImmediate(dir, (event) => {
                            log(`watchImmediate → ${JSON.stringify(event.type)} ${event.paths.join(", ")}`, "info");
                        }, { recursive: true });
                        return `正在监视（无合并窗口）${dir}`;
                    },
                },
                {
                    name: "unwatch()",
                    run: () => {
                        if (unwatch === null) {
                            return "没有活动的监视";
                        }
                        unwatch();
                        unwatch = null;
                        return "已停止监视";
                    },
                },
            ],
        },
        {
            title: "scope 边界（预期失败）",
            items: [
                {
                    name: "readTextFile('C:/Windows/win.ini')",
                    badge: "预期失败",
                    note: "命令已授权，但路径不在 scope 内 → path_not_allowed。",
                    run: () => readTextFile("C:/Windows/win.ini"),
                },
            ],
        },
    ],
};

function requireHandle(fn) {
    if (handle === null) {
        throw new Error("先执行 open('blob.bin')");
    }
    return fn(handle);
}
