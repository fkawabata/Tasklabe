using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Tasklabe.Animation;

/// <summary>
/// UI スレッドで時間をおいて行う処理。返すタイマーは、使う側がフィールドに持つ
/// （ローカル変数のままだと、鳴る前に回収されて動かなくなることがある）。止めるときは <see cref="DispatcherQueueTimer.Stop"/> を呼ぶ。
/// </summary>
public static class DispatcherQueueExtensions
{
    /// <summary>1 フレームの目安（60 Hz）。</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>時間をおいて 1 度だけ行う。</summary>
    public static DispatcherQueueTimer After(this DispatcherQueue queue, TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(action);
        var timer = queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            action();
        };
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Composition で動かせない値（スクロールの位置、ペインの幅など）を、決まった時間で減速しながら動かす。
    /// フレームごとに、進み具合（0〜1。減速の曲線を当てたもの）を <paramref name="step"/> に渡す。
    /// アニメーションが無効なら、すぐに 1 を渡して終える（そのときは null を返す）。
    /// </summary>
    public static DispatcherQueueTimer? Tween(this DispatcherQueue queue, TimeSpan duration, Action<double> step, Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(step);
        if (!Motion.IsEnabled || duration <= TimeSpan.Zero)
        {
            step(1);
            completed?.Invoke();
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        var timer = queue.CreateTimer();
        timer.Interval = Frame;
        timer.Tick += (t, _) =>
        {
            double progress = Math.Min(1, Stopwatch.GetElapsedTime(started) / duration);
            step(EaseOutCubic(progress));
            if (progress >= 1)
            {
                t.Stop();
                completed?.Invoke();
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>減速する 3 次の曲線（Fluent 2 の減速に近いもの）。</summary>
    internal static double EaseOutCubic(double progress) => 1 - Math.Pow(1 - progress, 3);
}
