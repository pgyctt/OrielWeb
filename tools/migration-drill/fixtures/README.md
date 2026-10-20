# 迁移夹具：线格式桩 → `mockIPC`

这个目录解决的问题只有一件：**迁移之后测试必红，而红的机制没人写下来过**。

## 症状

B7 演练里，`random-frame` 有 **32 处**测试直接桩 Tauri 的**线格式命令名**：

```js
// 迁移前（Tauri）
mockIPC((cmd) => {
    if (cmd === 'plugin:fs|write_file') return null;
});

// 或者更糟：完全跳过包，直接按协议回应
window.__TAURI_INTERNALS__.invoke = (cmd) => { /* ... */ };
```

迁移后应用代码改成了本库的命令名（`fs.writeFile`），但测试里的字符串还停在旧名字：

- **编译能过**——字符串谁都能写；
- **运行必红**——本库宿主没有 `plugin:fs|write_file` 这个命令；
- **红的位置在测试里**，于是第一反应往往是"这个库有问题"，而不是"我的测试还桩着旧名字"。

## 改法（两步）

1. 把桩里的命令名换成本库的「模块.方法」形式：`plugin:fs|write_file` → `fs.writeFile`。
   完整映射见 `docs/MIGRATION-FINDINGS.md` 的命令名表。
2. 用 `mockIPC` 而不是自己拼 `__TAURI_INTERNALS__`：

```js
import { invoke } from '@orielweb/api/core';
import { mockIPC, clearMocks } from '@orielweb/api/mocks';

mockIPC((cmd, args) => {
    switch (cmd) {
        case 'fs.writeFile': return null;
        case 'fs.readTextFile': return 'hi';
        default: throw new Error(`宿主没有命令「${cmd}」`);
    }
});

await invoke('fs.writeFile', { path: '/tmp/a.txt', contents: 'hi' });
clearMocks();
```

`default` 分支抛错是刻意的：**命令名拼错要在测试里当场炸**，而不是等真机上"什么都没发生"。

## 可执行的那部分

`mock-ipc.test.mjs` 在 CI 里跑（`node --test tools/migration-drill/fixtures/mock-ipc.test.mjs`）：

| 用例 | 钉住什么 |
|---|---|
| 正例 | `mockIPC` 按本库命令名分支，测试与生产用同一个字符串 |
| 反例 | 仍调 `plugin:fs\|write_file` 的失败形态是「宿主没有命令」——不是库的 bug |
| 自查 | 本文件除了反例那一处，不该再出现 `plugin:` 字面量 |

演练工装（`tools/migration-drill/drill.mjs`）在报告里按文件统计这类字面量，并标出哪些是测试文件——
那份统计给出的就是"这次迁移要改多少个测试"。
