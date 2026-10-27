using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Platform.Windows.WebView2.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using OrielWeb.Platform.Windows.WebView2.Interop;
using OrielWeb.Platform.Windows.WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionDevice;
using DCompPoint = OrielWeb.Platform.Windows.WebView2.Interop.WV2POINT;
using DCompTarget = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionTarget;
using DCompVisual = OrielWeb.Platform.Windows.WebView2.Interop.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>输入面：光标、鼠标、拖放、右键菜单、拖动窗口。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    /// <summary>渲染引擎内建右键菜单的策略；见 <see cref="OrielContextMenuPolicy"/>。</summary>
    public OrielContextMenuPolicy ContextMenuPolicy { get; set; }

    /// <summary>
    /// WebView2 内建右键菜单弹出前的回调（<c>ContextMenuRequested</c>）。
    /// </summary>
    /// <remarks>
    /// 这是<b>唯一</b>能"改内建菜单"而不是"另起一个菜单"的地方：过滤掉不想要的项之后，
    /// 菜单仍由 WebView2 自己弹，留下来的剪切/复制/粘贴因此保持着引擎实现的行为
    /// （能直接作用在页面选区上）。自己用 <see cref="ShowContextMenu"/> 造一个做不到这点——
    /// 粘贴需要把系统剪贴板送进页面，页面自己无此权限。
    /// <para>
    /// 整个回调包在 <c>try</c> 里：过滤菜单失败是小事，让进程倒在这里是大事。
    /// </para>
    /// </remarks>
    private void OnContextMenuRequested(object? sender, ICoreWebView2ContextMenuRequestedEventArgs args)
    {
        try
        {
            switch (ContextMenuPolicy)
            {
                case OrielContextMenuPolicy.Native:
                    return; // 平台原样

                case OrielContextMenuPolicy.Disabled:
                    args.Handled = true; // 抑制：右键不弹任何东西
                    return;

                default:
                    FilterEditingItems(args);
                    return;
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 过滤内建右键菜单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 只留下剪切 / 复制 / 粘贴。
    /// </summary>
    /// <remarks>
    /// <b>必须从后往前删</b>：集合按下标操作，正序删会让后面的项整体前移一格，
    /// 于是紧接着那一项被跳过——表现为"删了但不干净"，且只在中段有要删的项时出现。
    /// <para>
    /// 判断依据是 <c>Name</c>（<b>未本地化</b>的英文标识，如 <c>"copy"</c>），<b>不是</b>
    /// <c>Label</c>（那是给用户看的本地化文本，中文环境下是「复制」）——拿 Label 判断会在换语言时
    /// 静默失效。也**不是** <c>Kind</c>：那个枚举说的是控件种类
    /// （Command / CheckBox / Radio / Separator / Submenu），与"是不是复制"不是一回事。
    /// </para>
    /// </remarks>
    private static void FilterEditingItems(ICoreWebView2ContextMenuRequestedEventArgs args)
    {
        ICoreWebView2ContextMenuItemCollection? items = args.MenuItems;
        if (items is null)
        {
            return;
        }

        {
            for (uint i = items.Count; i > 0; i--)
            {
                ICoreWebView2ContextMenuItem? item = items.GetValueAtIndex(i - 1);
                if (item is null || !OrielContextMenuSupport.IsEditingWin32Name(item.Name))
                {
                    items.RemoveValueAtIndex(i - 1);
                }
            }

            // 一项都不剩时把菜单整个吃掉：否则会弹一个空框，看着像界面坏了
            if (items.Count == 0)
            {
                args.Handled = true;
            }
        }
    }

    /// <summary>
    /// 把窗口注册成 OLE 拖放目标（enter / over / leave / drop）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 失败时**不停用** <c>WM_DROPFILES</c>：那条路虽然只有 drop，但比"什么都收不到"好。
    /// 注册成功后 shell 的拖放源会走 OLE，<c>WM_DROPFILES</c> 不再发——所以这里不主动关掉
    /// <c>DragAcceptFiles</c>，留着正好当降级开关。
    /// </para>
    /// <para>
    /// 注册点必须在 <c>AllowExternalDrop = false</c> 之后（见调用处）：WebView2 没关掉自带拖放时
    /// 会自己占住窗口上的 <c>IDropTarget</c>，我们注册会拿到 <c>DRAGDROP_E_ALREADYREGISTERED</c>。
    /// </para>
    /// </remarks>
    private void RegisterDropTarget()
    {
        try
        {
            var target = new Win32DropTarget(_hwnd, OnDragEvent);
            if (!target.Register())
            {
                OrielDiagnostics.Report("[OrielWeb] 拖放降级为 WM_DROPFILES：只有 drop，没有 enter/over/leave。");
                return;
            }

            _dropTarget = target;
        }
        catch (Exception ex)
        {
            // 拖放是增强项：注册不成功也不该让窗口装配失败
            OrielDiagnostics.Report($"[OrielWeb] 注册拖放目标失败：{ex}");
        }
    }

    /// <summary>OLE 拖放事件 → 页面事件管道（<see cref="DragDrop"/>）+ B8 的 <see cref="FileDropped"/>。</summary>
    private void OnDragEvent(Win32DragEvent e)
    {
        OrielDragEventType type = e.Kind switch
        {
            Win32DragKind.Enter => OrielDragEventType.Enter,
            Win32DragKind.Over => OrielDragEventType.Over,
            Win32DragKind.Leave => OrielDragEventType.Leave,
            _ => OrielDragEventType.Drop,
        };

        DragDrop?.Invoke(new OrielDragEventArgs(type, e.Paths, e.X, e.Y));

        if (e.Kind == Win32DragKind.Drop && e.Paths.Count > 0)
        {
            // B8 已发布的公开事件：页面链路（drag.drop → tauri://drag-drop）因此完全不变
            FileDropped?.Invoke(new OrielFileDropEventArgs(e.Paths));
        }
    }

    /// <summary>
    /// 处理 <c>WM_DROPFILES</c>：从 HDROP 里逐个取路径，完成后 <c>DragFinish</c> 释放。
    /// </summary>
    /// <remarks>
    /// 取路径要**问两次**：第一次传 0 拿长度，第二次才把字符串读进缓冲区。
    /// 少一次就会得到一个截断的路径（或用固定大小缓冲区去猜）。HDROP 的生命周期由我们负责，
    /// 所以放在 <c>finally</c> 里释放——中途抛异常也不能漏。
    /// </remarks>
    private void HandleFileDrop(nint hDrop, nint lParam)
    {
        if (hDrop == 0)
        {
            return;
        }

        try
        {
            // 解析交给 Win32FileDropReader：OLE 的 drop 分支读的是同一种 HDROP，
            // 两处各写一份"取长度读两次"迟早会有一处写错，而错法是"路径被截断"。
            List<string> paths = Win32FileDropReader.Read(hDrop);
            if (paths.Count == 0)
            {
                return;
            }

            // lParam 是**屏幕**坐标（WM_DROPFILES 文档如此），换成客户区才与 OLE 那条路同口径
            int screenX = (short)(lParam & 0xFFFF);
            int screenY = (short)((lParam >> 16) & 0xFFFF);
            int x = 0;
            int y = 0;
            var origin = new POINT(0, 0);
            if (Win32.ClientToScreen(_hwnd, ref origin))
            {
                x = screenX - origin.X;
                y = screenY - origin.Y;
            }

            // 降级路径（OLE 注册失败时）也要把 drop 送到同一条页面事件管道
            DragDrop?.Invoke(new OrielDragEventArgs(OrielDragEventType.Drop, paths, x, y));
            FileDropped?.Invoke(new OrielFileDropEventArgs(paths));
        }
        finally
        {
            // HDROP 的生命周期归我们：中途抛异常也不能漏
            Win32.DragFinish(hDrop);
        }
    }

    /// <summary>
    /// 上下文菜单：构建 → 弹出（**阻塞**）→ 释放。
    /// </summary>
    /// <remarks>
    /// 每次调用都新建并销毁原生菜单：上下文菜单的内容通常随调用点而变（点在不同对象上），
    /// 缓存它反而要额外判断"内容变了没有"。
    /// </remarks>
    public void ShowContextMenu(
        IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
    {
        using Win32Menu? menu = Win32Menu.Build(items);
        if (menu is null)
        {
            return;
        }

        POINT? screenPoint = null;
        if (position is { } logical)
        {
            // 页面给的是**客户区逻辑像素**，Win32 要**屏幕物理像素**：两步都不能省。
            // 少第一步（乘 DPI）在 150% 屏上菜单会弹到偏左上四分之一的位置；
            // 少第二步（客户端 → 屏幕）窗口不在 (0,0) 时菜单会弹到屏幕左上角附近。
            uint dpi = Win32.GetDpiForWindow(_hwnd);
            var point = new POINT(
                LogicalPixels.ToPhysical((int)Math.Round(logical.X), dpi),
                LogicalPixels.ToPhysical((int)Math.Round(logical.Y), dpi));

            if (Win32.ClientToScreen(_hwnd, ref point))
            {
                screenPoint = point;
            }
        }

        OrielMenuItem? item = menu.Popup(_hwnd, screenPoint, autoDismissAfter);
        if (item is not null)
        {
            ActivateMenuItem(item);
        }
    }

    /// <summary>
    /// 结束正在跟踪的菜单（投递 <c>WM_CANCELMODE</c> 给菜单属主窗口）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不用 <c>EndMenu</c></b>：那个只结束"当前线程的菜单循环"，而本库的菜单走
    /// <c>TrackPopupMenuEx</c>——它用的是**自己创建的**模态循环，<c>EndMenu</c> 对它无效
    /// （实测：菜单照样挂着，自检 60 秒超时）。<c>WM_CANCELMODE</c> 会走进那个循环的取消路径。
    /// </para>
    /// <para>
    /// 用 <c>PostMessage</c> 而不是 <c>SendMessage</c>：<c>SendMessage</c> 要等目标窗口处理完
    /// 才返回，而目标窗口此刻正阻塞在菜单循环里——同步发送会把自己也堵住。
    /// </para>
    /// <para>
    /// 没有菜单在跟踪时这条消息是空操作，不报错（页面可能重复调 <c>menu.close</c>）。
    /// </para>
    /// </remarks>
    public void CloseContextMenu() => _ = Win32.PostMessageW(_hwnd, Win32Constants.WM_CANCELMODE, 0, 0);

    /// <summary>组合宿主不接收系统的光标设置，需把 WebView 当前光标自行应用到窗口。</summary>
    /// <remarks>
    /// <b>应用显式设过图标时不生效</b>（见 <see cref="SetCursorIcon"/>）：否则页面每更新一次
    /// CSS 光标就把那次设置冲掉，"设了却看不到"会变成一条无法解释的现象。
    /// </remarks>
    private void OnCursorChanged(object? sender, object? args)
    {
        if (_cursorIcon is not null)
        {
            return;
        }

        if (sender is ICoreWebView2CompositionController controller && controller.Cursor is { } cursor)
        {
            _ = Win32.SetCursor(cursor);
        }
    }

    /// <inheritdoc />
    public unsafe void SetCursorGrab(bool grab)
    {
        if (grab == _cursorGrabbed)
        {
            return;
        }

        _cursorGrabbed = grab;

        if (!grab)
        {
            _ = Win32.ClipCursor(null);
            return;
        }

        // 限制在**客户区**（不是整窗）：标题栏/边框本来就不属于页面
        if (!Win32.GetClientRect(_hwnd, out RECT client))
        {
            return;
        }

        var topLeft = new POINT { X = client.Left, Y = client.Top };
        var bottomRight = new POINT { X = client.Right, Y = client.Bottom };
        if (!Win32.ClientToScreen(_hwnd, ref topLeft) || !Win32.ClientToScreen(_hwnd, ref bottomRight))
        {
            return;
        }

        var clip = new RECT { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
        _ = Win32.ClipCursor(&clip);
    }

    /// <inheritdoc />
    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible)
        {
            return;
        }

        _cursorVisible = visible;

        // ShowCursor 是**计数器**而不是开关：多减一次就要多补一次，
        // 所以这里按状态去重（与 tao/Tauri 的做法一致）
        _ = Win32.ShowCursor(visible ? 1 : 0);
    }

    /// <inheritdoc />
    public void SetCursorIcon(OrielCursorIcon icon)
    {
        _cursorIcon = icon;
        _ = Win32.SetCursor(CursorHandle(icon));
    }

    /// <summary>把枚举映射到系统光标句柄（没有对应形状时用最接近的那一个）。</summary>
    /// <remarks>
    /// <b>降级是平台事实</b>：Windows 只有十来个系统光标，<c>copy</c> / <c>zoomIn</c> 这些
    /// 没有对应图。映射表与 docs/API.md 的降级表逐条对应——改这里要同步改文档。
    /// </remarks>
    private static nint CursorHandle(OrielCursorIcon icon) => icon switch
    {
        OrielCursorIcon.Crosshair => Win32.LoadCursorW(0, Win32Constants.IDC_CROSS),
        OrielCursorIcon.Hand => Win32.LoadCursorW(0, Win32Constants.IDC_HAND),
        OrielCursorIcon.Text or OrielCursorIcon.VerticalText => Win32.LoadCursorW(0, Win32Constants.IDC_IBEAM),
        OrielCursorIcon.Wait => Win32.LoadCursorW(0, Win32Constants.IDC_WAIT),
        OrielCursorIcon.Progress => Win32.LoadCursorW(0, Win32Constants.IDC_APPSTARTING),
        OrielCursorIcon.Help => Win32.LoadCursorW(0, Win32Constants.IDC_HELP),
        OrielCursorIcon.NotAllowed or OrielCursorIcon.NoDrop => Win32.LoadCursorW(0, Win32Constants.IDC_NO),
        OrielCursorIcon.Move or OrielCursorIcon.AllScroll or OrielCursorIcon.Cell =>
            Win32.LoadCursorW(0, Win32Constants.IDC_SIZEALL),
        OrielCursorIcon.Grab => Win32.LoadCursorW(0, Win32Constants.IDC_HAND),
        OrielCursorIcon.Grabbing => Win32.LoadCursorW(0, Win32Constants.IDC_HAND),
        OrielCursorIcon.EResize or OrielCursorIcon.WResize or OrielCursorIcon.EwResize
            or OrielCursorIcon.ColResize => Win32.LoadCursorW(0, Win32Constants.IDC_SIZEWE),
        OrielCursorIcon.NResize or OrielCursorIcon.SResize or OrielCursorIcon.NsResize
            or OrielCursorIcon.RowResize => Win32.LoadCursorW(0, Win32Constants.IDC_SIZENS),
        OrielCursorIcon.NeResize or OrielCursorIcon.SwResize or OrielCursorIcon.NeswResize =>
            Win32.LoadCursorW(0, Win32Constants.IDC_SIZENESW),
        OrielCursorIcon.NwResize or OrielCursorIcon.SeResize or OrielCursorIcon.NwseResize =>
            Win32.LoadCursorW(0, Win32Constants.IDC_SIZENWSE),

        // default / arrow / contextMenu / alias / copy / zoomIn / zoomOut：
        // 没有系统图，给标准箭头（写进文档的降级表）
        _ => Win32.LoadCursorW(0, Win32Constants.IDC_ARROW),
    };

    /// <inheritdoc />
    public void SetCursorPosition(double x, double y)
    {
        // 入参是**逻辑**像素；ClientToScreen 要的是物理像素（本进程是 per-monitor DPI aware）
        double scale = CurrentScaleFactor();
        var point = new POINT
        {
            X = (int)Math.Round(x * scale),
            Y = (int)Math.Round(y * scale),
        };

        if (Win32.ClientToScreen(_hwnd, ref point))
        {
            _ = Win32.SetCursorPos(point.X, point.Y);
        }
    }

    /// <inheritdoc />
    public void SetIgnoreCursorEvents(bool ignore)
    {
        if (ignore == _ignoreCursorEvents)
        {
            return;
        }

        _ignoreCursorEvents = ignore;

        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_EXSTYLE);
        nint updated = ignore
            ? style | Win32Constants.WS_EX_TRANSPARENT
            : style & ~Win32Constants.WS_EX_TRANSPARENT;

        _ = Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_EXSTYLE, updated);
    }

    /// <inheritdoc />
    public void StartResizeDragging(OrielResizeDirection direction)
    {
        // 与 BeginDrag 同一条路：WM_NCLBUTTONDOWN + HT* 让系统进入 resize 的模态循环。
        // 必须在鼠标按住期间调用（页面在 mousedown 里发起）。
        nint hit = direction switch
        {
            OrielResizeDirection.East => Win32Constants.HTRIGHT,
            OrielResizeDirection.North => Win32Constants.HTTOP,
            OrielResizeDirection.NorthEast => Win32Constants.HTTOPRIGHT,
            OrielResizeDirection.NorthWest => Win32Constants.HTTOPLEFT,
            OrielResizeDirection.South => Win32Constants.HTBOTTOM,
            OrielResizeDirection.SouthEast => Win32Constants.HTBOTTOMRIGHT,
            OrielResizeDirection.SouthWest => Win32Constants.HTBOTTOMLEFT,
            _ => Win32Constants.HTLEFT,
        };

        _ = Win32.SendMessageW(_hwnd, Win32Constants.WM_NCLBUTTONDOWN, (nuint)hit, 0);
    }

    /// <summary>
    /// 把窗口收到的鼠标消息注入组合托管的 WebView。组合模式下 WebView 收不到系统输入
    /// （它不是窗口），全部输入必须由宿主经 <c>SendMouseInput</c> 转发。
    /// </summary>
    private void ForwardMouseMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        ICoreWebView2CompositionController? controller = _compositionController?.Object;
        if (controller is null)
        {
            return;
        }

        if (message == Win32Constants.WM_MOUSEMOVE)
        {
            EnsureMouseLeaveTracking();
        }

        long packed = lParam.ToInt64();
        var point = new DCompPoint((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        if (point.x is < -32000 or > 32000 || point.y is < -32000 or > 32000)
        {
            return; // 窗口最小化等场景下的哨兵坐标
        }

        // 滚轮消息的坐标是屏幕坐标，其余是客户区坐标。ScreenToClient 需要 DirectN 的 POINT，
        // 而它非 blittable（无法用于源生成 P/Invoke），故用客户区原点自行换算。
        if (message is Win32Constants.WM_MOUSEWHEEL or Win32Constants.WM_MOUSEHWHEEL)
        {
            var origin = new POINT(0, 0);
            if (!Win32.ClientToScreen(hwnd, ref origin))
            {
                return;
            }
            point = new DCompPoint(point.x - origin.X, point.y - origin.Y);
        }

        var kind = message switch
        {
            Win32Constants.WM_MOUSEMOVE => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE,
            Win32Constants.WM_LBUTTONDOWN or Win32Constants.WM_LBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOWN,
            Win32Constants.WM_LBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_UP,
            Win32Constants.WM_RBUTTONDOWN or Win32Constants.WM_RBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOWN,
            Win32Constants.WM_RBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_UP,
            Win32Constants.WM_MBUTTONDOWN or Win32Constants.WM_MBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_DOWN,
            Win32Constants.WM_MBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_UP,
            Win32Constants.WM_MOUSEWHEEL => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_WHEEL,
            _ => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_HORIZONTAL_WHEEL,
        };

        // 滚轮的滚动量在 wParam 高 16 位（有符号），其余消息按按键状态折算虚拟键
        uint data = message is Win32Constants.WM_MOUSEWHEEL or Win32Constants.WM_MOUSEHWHEEL
            ? (uint)(short)((long)wParam >> 16)
            : 0;

        var keys = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;
        long state = (long)wParam;
        if ((state & Win32Constants.MK_LBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON;
        if ((state & Win32Constants.MK_RBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON;
        if ((state & Win32Constants.MK_MBUTTON) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_MIDDLE_BUTTON;
        if ((state & Win32Constants.MK_SHIFT) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_SHIFT;
        if ((state & Win32Constants.MK_CONTROL) != 0) keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_CONTROL;

        // 记下最后落点：WM_MOUSELEAVE 到达时指针已经在窗口外，系统给的坐标没有意义，
        // 补发 LEAVE 要用窗口内最后一次有效坐标（见 ForwardMouseLeave）。
        _lastMousePoint = point;
        _hasLastMousePoint = true;

        // 显式转 int：接口方法是 (int, int, uint, WV2POINT)，传枚举会命中 NativeExtensions 里
        // 那个同名同义的扩展（返回 void，内部 ThrowOnError），HRESULT 就此丢失。
        // 该扩展已删除，这里保留显式转换作为形状提醒。
        controller.SendMouseInput((int)kind, (int)keys, data, point);
    }

    /// <summary>补发一次 <c>COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE</c>。</summary>
    /// <remarks>
    /// 组合托管模式下 WebView 收不到任何系统输入（它不是窗口），"离开"同样要宿主转发；
    /// WebView2 只认这个事件来清 hover。坐标用窗口内最后一个有效落点——LEAVE 关心的是
    /// "离开了"这个事实，而不是离开后的坐标。窗口最小化、或还没转发过任何鼠标（没有可用落点、
    /// 也就没有可清的 hover）时不发，避免注入无意义的指针事件。
    /// </remarks>
    private void ForwardMouseLeave()
    {
        var controller = _compositionController?.Object;
        if (controller is null || !_hasLastMousePoint)
        {
            return;
        }

        controller.SendMouseInput(
            (int)COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE,
            (int)COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
            0,
            _lastMousePoint);
    }

    /// <summary>令光标离开窗口时补发一次 <c>WM_MOUSELEAVE</c>（Windows 默认不持续发送）。</summary>
    private void EnsureMouseLeaveTracking()
    {
        if (_trackingMouseLeave)
        {
            return;
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = 0x00000002, // TME_LEAVE
            hwndTrack = _hwnd,
        };
        if (Win32.TrackMouseEvent(ref track))
        {
            _trackingMouseLeave = true;
        }
    }

    public void BeginDrag()
    {
        // 经典技巧：释放鼠标捕获后发送 NC 按钮消息，让系统进入标题栏拖动模态循环
        if (Win32.ReleaseCapture())
        {
            _ = Win32.SendMessageW(_hwnd, Win32Constants.WM_NCLBUTTONDOWN, (nuint)Win32Constants.HTCAPTION, 0);
        }
    }

    // 流式拖动（macOS 用）；Windows 原生模态拖动已覆盖，此处 no-op
    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
    {
    }

    public void DragTo(double dx, double dy)
    {
    }

    public void EndDrag()
    {
    }
}
