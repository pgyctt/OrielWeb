# WebView2 与 DirectComposition 互操作绑定

主项目内的一个子模块（命名空间根 `OrielWeb.Platform.Windows.WebView2`），不单独成项目。
源码与 `OrielWeb.csproj` 一起编译，因此没有跨程序集边界，也没有额外的 ProjectReference。

## 为什么存在

上游 `WebView2Aot` 是 AOT 友好的，但它硬依赖 `DirectNAot`（22.8MB 程序集 / 17,635 个类型）
与 `DirectNAot.Extensions`。这两个包无法通过 `ExcludeAssets` 排除——`WebView2Aot` 的公共 API
（`IComObject<T>`、`PWSTR`、`RECT` 等）直接暴露了 DirectN 的类型。

本库实际只用 **26 个类型 / 69 个成员**，折算到原生 COM 是约 24 个对象接口 + 13 个回调接口。
把这个子集自己实现，换来的是：

| | 依赖树 | 说明 |
|---|---|---|
| 之前 | `WebView2Aot` 2.8MB → `DirectNAot` 22.8MB + `DirectNAot.Extensions` 0.97MB | restore 要下 28MB |
| 现在 | 仅 `Microsoft.Web.WebView2`（`ExcludeAssets="all"`，只用其中的 loader DLL） | 编译产物只含可达代码 |

## 目录结构

```
Interop/          原生 COM 接口：[GeneratedComInterface] + [Guid]，全 [PreserveSig] int
  NativeTypes.cs    WV2RECT / WV2POINT / COREWEBVIEW2_COLOR / EventRegistrationToken / PWSTR / HResult
  Interfaces.*.cs   按域拆分的对象接口
  Handlers.cs       13 个回调接口
  IStream.cs        标准 COM IStream（CreateWebResourceResponse 需要）
  Interfaces.DComposition.cs  IDCompositionDevice/Target/Visual + dcomp.dll 入口
Handlers/         [GeneratedComClass] 实现：CCW，负责把原生回调转成托管委托
Com/              IComObject<T> / ComObject：接管原生引用的托管句柄
Utilities/        WebView2Aot 同形状的便利层：属性、Task、string
  WebView2Utilities.cs  loader 提取/加载、QI、版本查询
  Functions.cs          环境创建（Task 化的完成处理器）
  Extensions.cs         IComObject<T> 上的便利扩展
  NativeExtensions.cs   裸接口上的同名便利扩展
  CoreWebView2Events.cs 事件容器（懒订阅）
```

## 五条必须守住的不变式

**1. vtable 顺序与枚举值都只能来自元数据。** COM 的早期绑定按 slot 序号寻址，少一个方法
或调换顺序就会调用到别的函数上——症状是随机崩溃或莫名其妙的 HRESULT。

枚举值错了则**不报任何错**，只是让某个交互静默失效。有过一次实例：`WHEEL` 写成 `0x020E`
（那是 `HORIZONTAL_WHEEL` 的值）、`LEAVE` 写成 `0x020A`（那是 `WHEEL` 的值）、
`VIRTUAL_KEYS` 整组错位——症状是"滚轮完全不滚"加"鼠标移出后 hover 残留"，
而当时全部 selftest 都是绿的（它们不覆盖鼠标注入）。

两类值都要从上游 `WebView2.dll` 的元数据取（`MethodDef` 顺序 / `FieldDef` 的 Constant blob），
不要照文档或记忆抄。枚举值另有 `tests/OrielWeb.Tests/WebView2EnumTests.cs` 逐值钉住，
改错会立刻红；新增枚举项时记得同步补一条断言。

还有一条相关禁令：**不要给已有接口方法加同名同义的扩展**。`NativeExtensions` 里曾有一个
`SendMouseInput(枚举, 枚举, uint, WV2POINT)`（返回 `void`），与接口的
`SendMouseInput(int, int, uint, WV2POINT)`（返回 HRESULT）并存。调用点传枚举时重载解析选中了
扩展，HRESULT 被扩展内部的 `ThrowOnError` 吞掉——排查"注入没反应"时完全看不到错误信息。
需要便利层就换个名字，或者干脆不加。

**2. 字符串 out 参数由调用方 `CoTaskMemFree`。** 所以原生层一律用 `out nint` 收，
再用 `PWSTR.ToStringAndDispose()` 读。不要改成 `out string` 让封送器决定释放策略。

**3. 回调的托管对象必须保活。** `[GeneratedComClass]` 的 CCW 只是原生侧的引用计数外壳；
托管对象一旦被 GC 回收，原生再回调就是野指针。`PendingHandlers` 负责异步调用期间的保活，
事件容器则用字段持有每个 handler。

**4. 包装原生指针时必须自己 `AddRef`。** `ComObject<T>` 的构造函数里那一行 `Marshal.AddRef`
不是冗余。实测（.NET 10）`ComInterfaceMarshaller<T>.ConvertToManaged` 对同一指针的反复包装
（事件参数、QI 结果、多窗口共享的 Environment 都会命中）**不保证新增引用**；若此时 `Dispose`
仍无条件 `Release`，净效果就是 -1，原生对象被提前销毁。

症状很难往引用计数上想：不是立刻崩溃，而是在**进程退出或某次 GC** 时于
`System.Runtime.InteropServices.Marshalling.ComObject.Finalize()` 里 Access Violation
（`0xC0000409`），或者后续调用莫名返回 `E_UNEXPECTED`。

判据是可以用 `Marshal.AddRef` 量出来的：**构造净 +1、Dispose 净 -1、整体净变化 0**。
改这一行之前先跑一遍这个测量。

**5. `TargetCompatibleBrowserVersion` 不能留空。** WebView2 的 loader 对它的判定比文档严格
（实测 evergreen 运行时 155.0.4283.62）：

| 取值 | 结果 |
|---|---|
| `null` 或 `""` | `E_INVALIDARG`（0x80070057，界面上就是 "Value does not fall within the expected range"） |
| 低于已安装运行时（如 `1.0.0.0`） | `0x80070002`，它按固定版本去装那个运行时，找不到 |
| ≥ 已安装运行时 | 正常走 evergreen |

所以默认值取**当前已安装版本**（懒求值，见 `CoreWebView2EnvironmentOptions.TargetCompatibleBrowserVersion`），
既不是空串，也不是硬编码的 SDK 版本号。上游 WebView2Aot 选的是硬编码（1.6.1 里为 `152.0.4191.47`），
代价是运行时低于该值时创建失败。

## 与 WebView2Aot 的 API 对照

| WebView2Aot | 这里 |
|---|---|
| `WebView2.ICoreWebView2*` | `OrielWeb.Platform.Windows.WebView2.Interop.ICoreWebView2*` |
| `WebView2.Utilities.*` 扩展 | `OrielWeb.Platform.Windows.WebView2.Utilities.*`，同名同形状 |
| `DirectN.Extensions.Com.IComObject<T>` | `OrielWeb.Platform.Windows.WebView2.Com.IComObject<T>` |
| `DirectN.RECT` / `DirectN.POINT` | `WV2RECT` / `WV2POINT`（改名以避免与宿主自有的 Win32 类型冲突） |

契约是：**上层只改 using，不改调用点**。`src/OrielWeb/Platform/Windows` 下的
12 个文件就是这么迁过来的。

## 新增一个 WebView2 接口的步骤

1. 从上游取 IID 与方法顺序：

```powershell
$p = "$env:USERPROFILE/.nuget/packages/webview2aot/1.6.1/lib/net10.0/WebView2.dll"
$pe = [System.Reflection.PortableExecutable.PEReader]::new([System.IO.File]::OpenRead($p))
$md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
# 遍历 TypeDefinitions，读 [Guid] 的 CustomAttribute blob（ASCII 字符串形式）
# 方法名按 MethodDef 顺序即 vtable 顺序
```

2. 在 `Interop/Interfaces.*.cs` 里按顺序声明，方法一律 `[PreserveSig] int`；
   字符串 in 参数用 `string`（接口已标 `StringMarshalling.Utf16`），out 参数用 `out nint`。

3. 需要向上层暴露便利方法时，**两个文件都要加**：`Extensions.cs`（`IComObject<T>` 接收器）
   与 `NativeExtensions.cs`（裸接口接收器）。宿主代码两种持有方式都有。

4. 跑一遍冒烟：`Functions.CreateCoreWebView2EnvironmentWithOptionsAsync` +
   `get_BrowserVersionString` + QI 到新接口。这三步能同时验证 loader、vtable slot、
   完成处理器 CCW 与 IID 是否正确。

## 已知边界

- 只覆盖本库用到的成员。`ICoreWebView2` 里 `CapturePreview`、`AddHostObjectToScript`、
  `CallDevToolsProtocolMethod` 等方法已按原样声明但未封装便利层，需要时补。
- `ContextMenuRequested` 在 `ICoreWebView2_11`、`Profile` 在 `_13`，所以版本链声明到 `_13`。
  更深的接口（`_14` 以上）未声明。
- 事件参数对象在回调返回后即失效。若将来需要跨回调持有，必须显式 `AddRef`。
