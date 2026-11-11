; Unshipped analyzer releases

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ORIELWEB101 | OrielWeb | Error | 命令名不可用（非字符串常量 / 空白 / 含控制字符）
ORIELWEB102 | OrielWeb | Error | 命令名冲突（同程序集内同名 [OrielCommand]）
ORIELWEB103 | OrielWeb | Error | 命令方法不可访问（生成的路由无法调用）
ORIELWEB104 | OrielWeb | Error | 命令宿主不能是泛型类型
ORIELWEB105 | OrielWeb | Error | 命令参数不支持 ref/out
ORIELWEB106 | OrielWeb | Error | 命令名落在保留前缀 win./tray.（整族绕过能力配置，其中内建命令又先于应用路由），构建期报错
ORIELWEB107 | OrielWeb | Error | 命令名与库内建的 event.emit 重合（会被内建事件命令静默遮蔽；运行期注册表同样会拦）
ORIELWEB108 | OrielWeb | Error | 命令方法不能是泛型（路由类无类型参数，生成的 T 会在 .g.cs 里报 CS0246；IPC 参数不带类型实参）
ORIELWEB110 | OrielWeb | Error | 命令参数不支持 C# 默认值（本库用可空类型表达可选参数；`int? a = null` 与 `CancellationToken ct = default` 这类无害写法放行）
