using OrielWeb.Ipc;

namespace OrielDemo;

public sealed record TodoItem(int Id, string Text, bool Done, string CreatedAt);

public sealed record SysInfo(string Platform, string Version, int TodoCount);

/// <summary>示例命令类：JS 侧经 window.oriel.invoke('todo.xxx', {...}) 调用。</summary>
public sealed partial class TodoCommands
{
    private readonly List<TodoItem> _items = [];
    private int _nextId;

    [OrielCommand("todo.list")]
    public TodoItem[] List() => [.. _items];

    [OrielCommand("todo.add")]
    public TodoItem Add(string text)
    {
        var item = new TodoItem(_nextId++, text, Done: false, CreatedAt: DateTime.Now.ToString("HH:mm:ss"));
        _items.Add(item);
        return item;
    }

    [OrielCommand("todo.toggle")]
    public TodoItem Toggle(int id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id)
            ?? throw new OrielIpcException($"不存在 id={id} 的待办事项。");
        var updated = item with { Done = !item.Done };
        _items[_items.IndexOf(item)] = updated;
        return updated;
    }

    [OrielCommand("todo.remove")]
    public void Remove(int id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id)
            ?? throw new OrielIpcException($"不存在 id={id} 的待办事项。");
        _items.Remove(item);
    }

    [OrielCommand("sys.info")]
    public static SysInfo Info(int extra)
    {
        return new SysInfo(
            Platform: CurrentPlatformName(),
            Version: typeof(TodoCommands).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            TodoCount: extra);
    }

    /// <summary>当前平台名（与桥接 JS 的 window.oriel.platform 取值保持一致）。</summary>
    private static string CurrentPlatformName() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";
}
