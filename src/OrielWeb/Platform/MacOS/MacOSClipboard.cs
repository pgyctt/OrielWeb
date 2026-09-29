using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// 剪贴板读写（NSPasteboard）。文本用 <c>public.utf8-plain-text</c>；HTML 用 <c>public.html</c>。
/// 写 HTML 时同时写一份纯文本回退，供只认文本的应用粘贴。
/// </summary>
/// <remarks>
/// 与窗口无关，放在分部文件里。三个平台里这一份最短：NSPasteboard 一个 <c>stringForType:</c> /
/// <c>setString:forType:</c> 就覆盖了文本与 HTML，不像 Windows 要自己拼 CF_HTML 头。
/// </remarks>
internal sealed partial class MacOSWindowHost
{
    private const string Utf8TextType = "public.utf8-plain-text";
    private const string HtmlType = "public.html";

    public string? GetClipboardText() => ReadPasteboard(Utf8TextType);

    public string? GetClipboardHtml() => ReadPasteboard(HtmlType);

    public void SetClipboardText(string text)
    {
        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return;
        }

        _ = ObjCRuntime.SendId(pasteboard, ObjCRuntime.Sel("clearContents"));
        SetPasteboardString(pasteboard, Utf8TextType, text);
    }

    public void SetClipboardHtml(string html, string? plainTextFallback)
    {
        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return;
        }

        _ = ObjCRuntime.SendId(pasteboard, ObjCRuntime.Sel("clearContents"));
        SetPasteboardString(pasteboard, HtmlType, html);
        // 纯文本回退：不认 public.html 的应用也能粘到内容
        SetPasteboardString(pasteboard, Utf8TextType, plainTextFallback ?? html);
    }

    private static nint GeneralPasteboard()
        => ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSPasteboard"), ObjCRuntime.Sel("generalPasteboard"));

    /// <summary>
    /// 读指定类型；不存在该类型或内容为空时返回 null。
    /// 注意 setString:forType: 要的是 NSString*（不是 char*），所以两边都得用 MakeNSString。
    /// </summary>
    private static string? ReadPasteboard(string type)
    {
        nint pasteboard = GeneralPasteboard();
        if (pasteboard == 0)
        {
            return null;
        }

        nint value = ObjCRuntime.SendIdObj(
            pasteboard, ObjCRuntime.Sel("stringForType:"), ObjCRuntime.MakeNSString(type));
        return value == 0 ? null : ObjCRuntime.ToManagedString(value);
    }

    private static void SetPasteboardString(nint pasteboard, string type, string value)
        => ObjCRuntime.SendVoidObjObj(
            pasteboard,
            ObjCRuntime.Sel("setString:forType:"),
            ObjCRuntime.MakeNSString(value),
            ObjCRuntime.MakeNSString(type));
}
