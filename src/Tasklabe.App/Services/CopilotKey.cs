using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Tasklabe.App.Services;

/// <summary>
/// Copilot キーの状態（要件 F-UI-MY-08、技術設計書 2.3.3 節）。Tasklabe が動いていないときは URI の起動で、
/// 動いているときはウィンドウへのメッセージ（fast path）で受け取る。
/// </summary>
public static class CopilotKey
{
    /// <summary>押した（短く押して離した）。</summary>
    public const string Tap = "Tap";

    /// <summary>長押しを始めた。</summary>
    public const string Down = "Down";

    /// <summary>長押しを離した。</summary>
    public const string Up = "Up";

    /// <summary>Windows が Copilot キーの状態を知らせるメッセージ（WM_APP の範囲から選ぶ）。</summary>
    private const uint Message = 0x8000 + 0x0001;

    /// <summary>メッセージの wParam と状態の対応。Package/AppxManifest.xml の MessageWParam にそろえる。</summary>
    private static readonly string[] States = [Tap, Down, Up];

    /// <summary>Copilot キーのメッセージを受け取る窓口を示すプロパティ。</summary>
    private static readonly PropertyKey FastPathKey = new(new Guid("38652BCA-4329-4E74-86F9-39CF29345EEA"), 2);

    /// <summary>サブクラスの手続きを、ウィンドウがあるあいだ回収されないように持つ。</summary>
    private static readonly Dictionary<nint, SubclassProc> Procs = [];

    /// <summary>
    /// ウィンドウで Copilot キーのメッセージを受け取る。Tasklabe が動いているあいだは、Windows はこのメッセージで知らせ、
    /// URI の起動はしない（受け取るウィンドウがないと、ウィンドウの表示と最小化を切り替えるだけになる）。
    /// </summary>
    public static void Listen(Window window, Action<string> pressed)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(pressed);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
            var key = FastPathKey;
            var value = new PropVariant { Type = VtUInt, UInt = Message };
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
            Marshal.ReleaseComObject(store);
        }
        catch (Exception ex)
        {
            AppLog.Error("CopilotKey", ex);
            return;
        }

        // Windows は、知らせたときに前面だったウィンドウへ、続けて最小化を送る（表示と最小化の切り替え）。
        // 知らせに応えて開いたウィンドウの後ろで本体が隠れないよう、その最小化を 1 度だけ受け流す。
        // 次にこのウィンドウがアクティブになったら、もう受け流さない
        bool skipMinimize = false;
        SubclassProc proc = (h, message, wParam, lParam, id, data) =>
        {
            switch (message)
            {
                case Message when wParam < (nuint)States.Length:
                    skipMinimize = GetForegroundWindow() == h;
                    pressed(States[wParam]);
                    return 0;
                case WmSysCommand when (wParam & 0xFFF0) == ScMinimize && skipMinimize:
                    skipMinimize = false;
                    return 0;
                case WmActivate when (wParam & 0xFFFF) != 0:
                    skipMinimize = false;
                    break;
            }

            return DefSubclassProc(h, message, wParam, lParam);
        };
        Procs[hwnd] = proc;
        SetWindowSubclass(hwnd, proc, 1, 0);
        window.Closed += (_, _) =>
        {
            RemoveWindowSubclass(hwnd, proc, 1);
            Procs.Remove(hwnd);
        };
    }

    private const ushort VtUInt = 23;
    private const uint WmActivate = 0x0006;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMinimize = 0xF020;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public uint UInt;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, out IPropertyStore store);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
