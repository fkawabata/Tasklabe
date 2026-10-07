using Microsoft.UI.Dispatching;

namespace Tasklabe.App.Services;

/// <summary>
/// 最後に呼んでから一定の時間がたったら、1 回だけ実行する（入力の区切りを待って保存する、知らせを時間で閉じるなど）。
/// タイマーは参照を持っておかないと発火前に回収されるため、この部品が持ち続ける。
/// </summary>
public sealed class Debouncer(DispatcherQueue queue, TimeSpan delay)
{
    private DispatcherQueueTimer? _timer;
    private Action? _action;

    /// <summary>待っているか（まだ実行していない呼び出しがあるか）。</summary>
    public bool IsPending => _timer?.IsRunning == true;

    /// <summary>待ち直して、時間がたったら action を実行する。前に渡した action は実行しない。</summary>
    public void Run(Action action)
    {
        _action = action;
        if (_timer is null)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => _action?.Invoke();
        }

        _timer.Stop();
        _timer.Start();
    }

    public void Cancel() => _timer?.Stop();
}
