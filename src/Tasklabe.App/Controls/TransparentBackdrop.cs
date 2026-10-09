using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Tasklabe.App.Controls;

/// <summary>
/// ウィンドウの背景を透明にする。中に置いた面の外側から、後ろのデスクトップが見える（タスクの追加の浮いた板）。
/// </summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_compositor;
    private static object? s_queueController;
    private static nint s_black;

    /// <summary>
    /// ウィンドウを透明にできる形にする。DWM の枠をクライアント領域へ広げて後ろを透かし、背景の消去を黒（透明として合成される）で塗り、
    /// Windows が描く縁と角の丸めを外す。背景にこのクラスを当てたウィンドウで、表示する前に呼ぶ。
    /// </summary>
    public static void Prepare(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // タイトルバーを外しても残る細い枠（WS_DLGFRAME など）を外す。残すと外周に 1 px の線が出る
        SetWindowLongPtr(hwnd, GwlStyle, GetWindowLongPtr(hwnd, GwlStyle) & ~(WsBorder | WsDlgFrame | WsThickFrame));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        ConfigureDwm(hwnd);
        int none = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
        int doNotRound = 1;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));

        SubclassProc proc = (h, message, wParam, lParam, id, data) =>
        {
            switch (message)
            {
                case WmEraseBkgnd:
                    ClearBackground(h, (nint)wParam);
                    return 1;
                case WmDwmCompositionChanged:
                    ConfigureDwm(h);
                    return 0;
                default:
                    return DefSubclassProc(h, message, wParam, lParam);
            }
        };
        Procs[hwnd] = proc;
        SetWindowSubclass(hwnd, proc, 2, 0);
        window.Closed += (_, _) =>
        {
            RemoveWindowSubclass(hwnd, proc, 2);
            Procs.Remove(hwnd);
        };

        var hdc = GetDC(hwnd);
        ClearBackground(hwnd, hdc);
        ReleaseDC(hwnd, hdc);
    }

    private static void ConfigureDwm(nint hwnd)
    {
        var margins = default(Margins);
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var region = CreateRectRgn(-2, -2, -1, -1);
        var blur = new BlurBehind { Flags = 3, Enable = true, Region = region };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        DeleteObject(region);
    }

    private static void ClearBackground(nint hwnd, nint hdc)
    {
        if (GetClientRect(hwnd, out var rect))
        {
            s_black = s_black != 0 ? s_black : CreateSolidBrush(0);
            FillRect(hdc, ref rect, s_black);
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(connectedTarget);
        base.OnTargetConnected(connectedTarget, xamlRoot);
        connectedTarget.SystemBackdrop = Compositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ArgumentNullException.ThrowIfNull(disconnectedTarget);
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    /// <summary>背景の塗りを作る Compositor（Windows.UI.Composition）。スレッドに Windows.System の DispatcherQueue が要る。</summary>
    private static Windows.UI.Composition.Compositor Compositor()
    {
        if (s_compositor is not null)
        {
            return s_compositor;
        }

        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            s_queueController = controller;
        }

        return s_compositor = new Windows.UI.Composition.Compositor();
    }

    private const int GwlStyle = -16;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmDwmCompositionChanged = 0x031E;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    /// <summary>サブクラスの手続きを、ウィンドウがあるあいだ回収されないように持つ。</summary>
    private static readonly Dictionary<nint, SubclassProc> Procs = [];

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;
        public nint Region;
        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref Rect rect, nint brush);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);
}
