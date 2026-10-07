; Unshipped analyzer releases

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ORIELWEB101 | OrielWeb | Error | 命令名不可用（非字符串常量 / 空白 / 含控制字符）
ORIELWEB102 | OrielWeb | Error | 命令名冲突（同程序集内同名 [OrielCommand]）
ORIELWEB103 | OrielWeb | Error | 命令方法不可访问（生成的路由无法调用）
ORIELWEB104 | OrielWeb | Error | 命令宿主不能是泛型类型
ORIELWEB105 | OrielWeb | Error | 命令参数不支持 ref/out
ORIELWEB106 | OrielWeb | Warning | 命令名落在保留前缀 win.（会被内建命令静默遮蔽）
