# drag

原文：[`tauri/examples/drag`](https://github.com/tauri-apps/tauri/tree/dev/examples/drag)（Apache-2.0 / MIT）。

原文是一个 `data-tauri-drag-region` 的语义测试矩阵（16 条用例）。本库的拖动由**桥接脚本**接管，
页面侧只剩一个属性，所以这份移植保留能直接对照的 6 条，并把两种本库没有的取值换成等价写法。

## 页面侧的变化

```html
<!-- Tauri -->
<div data-tauri-drag-region>标题栏</div>

<!-- 本库 -->
<div data-oriel-drag-region>标题栏</div>
```

**拖动代码整段删除。** Tauri 的 `drag.js` 要做的事——监听 `pointerdown`、判断
clickable 标签与交互 `role`、调 `plugin:window|start_dragging`——都在桥接脚本里：

| 行为 | Tauri | 本库 |
|---|---|---|
| 按下并移动 → 移动窗口 | 页面脚本 + `start_dragging` | 桥接脚本 + `win.drag*` |
| 双击 → 最大化 / 还原 | 页面脚本 + `internal_toggle_maximize` | 桥接脚本 + `win.toggleMaximize` |
| 交互元素不被接管 | 页面维护标签/role 表 | 桥接脚本内建表 |
| 权限 | ACL 放行 `plugin:window\|start_dragging` | **不需要**（`win.*` 始终放行） |

把这段放进库里的理由很实际：三平台的拖动形态差别很大（Windows 的原生模态拖动会吞掉后续点击、
Wayland 下必须等指针动过阈值才能把移动交给合成器），每个应用各写一遍等于各踩一遍。

## 三种取值怎么对应

| Tauri | 本库 |
|---|---|
| `data-tauri-drag-region`（裸属性） | `data-oriel-drag-region` |
| `="deep"`（子树也算拖动） | 裸属性即等价——本库默认就是整棵子树 |
| `="false"`（关掉） | 给要排除的那块加 `data-oriel-no-drag` |

内建排除表：`button` / `input` / `select` / `textarea` / `a` / `contenteditable`
以及带 `data-oriel-no-drag` 的元素。**不认 `role`**——原文用例 11 / 15 / 16 测的
`role="button"` 这类写法在这里不会自动排除，要排除请用 no-drag 属性。

## 宿主侧的另一种写法

不写属性也可以由宿主指定选择器：

```csharp
.AddWindow(w => w.WithFrameless().WithDragRegion("#titlebar"))
```

两个来源取并集。适合"页面是第三方产物、改不了 DOM"的场景。

## 跑

```bash
dotnet run --project samples/TauriExamples/Drag/TauriDrag.csproj
```

窗口是无边框的：标题栏整条可拖、双击最大化；右上角是最小化与关闭（它们同时也是
"拖动区域里的按钮不会被吞掉"的活证据）。
