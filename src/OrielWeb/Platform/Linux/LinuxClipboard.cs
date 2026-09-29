using System.Runtime.InteropServices;
using System.Text;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// 剪贴板读写（GTK）。文本用 <c>gtk_clipboard_set_text</c> / <c>wait_for_text</c>；
/// HTML 走自定义 target <c>text/html</c>——写只能用 <c>gtk_clipboard_set_with_data</c>
/// （声明式拥有：对方来要时才回调取数据），读用 <c>wait_for_contents</c>。
/// </summary>
/// <remarks>
/// GTK 的剪贴板是 X11 的"选择（selection）"语义：内容由**所有者进程按需提供**，而不是立刻拷到某处。
/// 所以写完以后所有者必须继续存活，别人才拿得到内容（同一进程内读写没问题）。
/// 与窗口无关，放在分部文件里。
///
/// 写 HTML 时会**同时**声明两个文本 target（<c>UTF8_STRING</c> 与 <c>text/plain;charset=utf-8</c>）：
/// 前者是 GTK 生态的惯例（<c>gtk_clipboard_wait_for_text</c> 找的就是它），后者照顾按 MIME 取内容的程序。
/// 缺了文本 target 时，只认文本的应用（以及我们自己的 <see cref="GetClipboardText"/>）会读不到回退内容——
/// 这是最初漏掉、由自检抓出来的一处缺口。
/// </remarks>
internal sealed partial class LinuxWindowHost
{
    private const string ClipboardSelection = "CLIPBOARD";
    private const string HtmlTarget = "text/html";
    private const string Utf8StringTarget = "UTF8_STRING";
    private const string PlainTextTarget = "text/plain;charset=utf-8";

    /// <summary>GtkTargetEntry.info：取数据时用它区分对方要的是 HTML 还是文本。</summary>
    private const uint InfoHtml = 1;
    private const uint InfoText = 2;

    /// <summary>进程级的 GtkTargetEntry 数组（只分配一次；GTK 不保证拷贝它，长期有效最安全）。</summary>
    private static nint s_clipboardTargets;

    public string? GetClipboardText()
    {
        nint clipboard = ClipboardHandle();
        if (clipboard == 0)
        {
            return null;
        }

        nint textPtr = GtkNative.GtkClipboardWaitForText(clipboard);
        if (textPtr == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(textPtr);
        }
        finally
        {
            // wait_for_text 返回的是需要释放的副本（归调用方所有）
            GtkNative.GFree(textPtr);
        }
    }

    public void SetClipboardText(string text)
    {
        nint clipboard = ClipboardHandle();
        if (clipboard != 0)
        {
            GtkNative.GtkClipboardSetText(clipboard, text, -1);
        }
    }

    public string? GetClipboardHtml()
    {
        nint clipboard = ClipboardHandle();
        if (clipboard == 0)
        {
            return null;
        }

        // onlyIfExists=1：剪贴板里没有 text/html 时立刻返回，不必等
        nint htmlAtom = GtkNative.GdkAtomIntern(HtmlTarget, 1);
        if (htmlAtom == 0)
        {
            return null;
        }

        nint selection = GtkNative.GtkClipboardWaitForContents(clipboard, htmlAtom);
        if (selection == 0)
        {
            return null;
        }

        try
        {
            int length = GtkNative.GtkSelectionDataGetLength(selection);
            nint data = GtkNative.GtkSelectionDataGetData(selection);
            if (length <= 0 || data == 0)
            {
                return null;
            }

            byte[] bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            // X11 上 text/html 的约定是 UTF-8（charset 可另标，这里按惯例取 UTF-8）
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            GtkNative.GtkSelectionDataFree(selection);
        }
    }

    public unsafe void SetClipboardHtml(string html, string? plainTextFallback)
    {
        nint clipboard = ClipboardHandle();
        if (clipboard == 0)
        {
            return;
        }

        // 先清掉旧的：若上一个所有者也是本进程，GTK 会在此触发它的 clear 回调，释放它持有的句柄
        GtkNative.GtkClipboardClear(clipboard);

        var payload = new ClipboardPayload(html, plainTextFallback ?? html);
        nint handle = GCHandle.ToIntPtr(GCHandle.Alloc(payload));
        int ok = GtkNative.GtkClipboardSetWithData(
            clipboard,
            ClipboardTargets(),
            3,
            (nint)(delegate* unmanaged<nint, nint, uint, nint, void>)&OnClipboardGetData,
            (nint)(delegate* unmanaged<nint, nint, void>)&OnClipboardClear,
            handle);

        if (ok == 0)
        {
            // 设置失败：GTK 不会回调 clear，句柄由我们自己释放；并退化为纯文本，至少不丢内容
            GCHandle.FromIntPtr(handle).Free();
            GtkNative.GtkClipboardSetText(clipboard, payload.Text, -1);
        }
    }

    private static nint ClipboardHandle()
        => GtkNative.GtkClipboardGet(GtkNative.GdkAtomIntern(ClipboardSelection, 0));

    /// <summary>声明式剪贴板的内容载体：GCHandle 一次只装一个对象，所以把两份内容包在一起。</summary>
    private sealed record ClipboardPayload(string Html, string Text);

    /// <summary>
    /// 构造 GtkTargetEntry 数组（HTML 一项 + 文本两项）。
    /// 结构布局：<c>{ gchar* target; guint flags; guint info; }</c> —— 64 位下 8+4+4 = 16 字节。
    /// </summary>
    private static nint ClipboardTargets()
    {
        nint existing = Volatile.Read(ref s_clipboardTargets);
        if (existing != 0)
        {
            return existing;
        }

        nint block = Marshal.AllocHGlobal(16 * 3);
        WriteTargetEntry(block, 0, HtmlTarget, InfoHtml);
        WriteTargetEntry(block, 1, Utf8StringTarget, InfoText);
        WriteTargetEntry(block, 2, PlainTextTarget, InfoText);
        Volatile.Write(ref s_clipboardTargets, block);
        return block;
    }

    private static void WriteTargetEntry(nint block, int index, string target, uint info)
    {
        nint entry = block + (index * 16);
        Marshal.WriteIntPtr(entry, 0, Marshal.StringToCoTaskMemUTF8(target));
        Marshal.WriteInt32(entry, 8, 0);          // flags
        Marshal.WriteInt32(entry, 12, (int)info); // info
    }

    /// <summary>GTK 来取数据时调用（GtkClipboardGetFunc）；按 info 决定给 HTML 还是文本。</summary>
    [UnmanagedCallersOnly]
    private static void OnClipboardGetData(nint clipboard, nint selectionData, uint info, nint userData)
    {
        try
        {
            if (userData == 0 || GCHandle.FromIntPtr(userData).Target is not ClipboardPayload payload)
            {
                return;
            }

            string content = info == InfoText ? payload.Text : payload.Html;
            string typeName = info == InfoText ? Utf8StringTarget : HtmlTarget;

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            nint data = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                _ = GtkNative.GtkSelectionDataSet(
                    selectionData, GtkNative.GdkAtomIntern(typeName, 0), 8, data, bytes.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch
        {
            // 异常不得穿越 GTK 回调边界
        }
    }

    /// <summary>剪贴板所有权被夺走时调用（GtkClipboardClearFunc）：释放 user_data 里的 GC 句柄。</summary>
    [UnmanagedCallersOnly]
    private static void OnClipboardClear(nint clipboard, nint userData)
    {
        try
        {
            if (userData != 0)
            {
                GCHandle.FromIntPtr(userData).Free();
            }
        }
        catch
        {
            // 同上：不外泄异常
        }
    }
}
