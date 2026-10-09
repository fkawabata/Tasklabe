using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Tasklabe.App;

/// <summary>
/// 起動の入口（技術設計書 2.3 節）。同じ場所の Tasklabe は 1 つだけ動かし、2 つ目以降の起動（Copilot キーなど）は
/// 動いているほうへ渡して終わる。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// 同じ場所の Tasklabe がすでに動いていれば、この起動を渡す。置き場所ごとに分けるのは、開発用に別の場所で動かす Tasklabe と
    /// 取り合わないようにするため。
    /// </summary>
    private static bool RedirectToRunningInstance()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant()));
        var key = AppInstance.FindOrRegisterForKey("Tasklabe-" + Convert.ToHexString(hash, 0, 8));
        if (key.IsCurrent)
        {
            return false;
        }

        // 渡した先がウィンドウを前面に出せるよう、この起動が持つ前面に出す権利を譲る
        AllowSetForegroundWindow(key.ProcessId);

        // 渡し終えるのを待つあいだも COM の呼び出しを処理できるよう、STA のまま CoWaitForMultipleObjects で待つ
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
        Task.Run(async () =>
        {
            await key.RedirectActivationToAsync(args);
            done.Set();
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] nint[] handles, out uint index);
}
