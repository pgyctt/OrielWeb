using System.Runtime.CompilerServices;

// demo 是唯一的消费者：它的 Program.cs 保留一个 `--selftest` 转发入口，
// 调本库的 internal 自检类。这样自检的实现细节（步骤状态机、看门狗、结论行）
// 不会变成公开 API 表面——本库不是给别人引用的，它是本仓库的契约测试夹具。
//
// 若将来出现第二个宿主（例如独立 conformance exe），把需要的入口改成 public，
// 而不是继续往这个列表里加程序集名。
[assembly: InternalsVisibleTo("OrielDemo")]
