namespace Tasklabe.Animation;

/// <summary>
/// ばねの動き。落ち着くまでの見た目の時間（秒）と、行き過ぎの大きさ（0 なら行き過ぎない）で決める。
/// </summary>
public readonly record struct Spring(double VisualDuration, double Bounce)
{
    /// <summary>
    /// Composition に渡す Period。Composition の Period は固有角振動数の逆数として働くため、
    /// 見た目の時間から求めた 1 往復の時間（見た目の時間の 1.2 倍）を 2π で割る。
    /// </summary>
    public TimeSpan Period => TimeSpan.FromSeconds(VisualDuration * 1.2 / (2 * Math.PI));

    public float Damping => (float)(1 - Bounce);

    /// <summary>
    /// 動く距離（px）に合わせたばね。ばねは残りの距離を一定の割合で縮めるため、同じばねでは距離が長いほど止まって見えるまでが長くなる
    /// （大きな面が育つときに、もっさりして見える）。残りの目安を e^(−ζωt) とし、距離 d が 1 px を切る t = ln(d) ÷ ζω を
    /// 見た目の時間にそろえて、それより遅くならないように固さを決める。動きの速さを決めるための目安であり、
    /// 着き切る必要があるとき（動き終えた時刻に別の要素へ切り替えるとき）は <see cref="Landing"/> を使う。
    /// </summary>
    public Spring ForDistance(double distance)
    {
        // Period から決まる固有角振動数（ω = 2π ÷ 見た目の時間 ÷ 1.2）と同じ換算で、見た目の時間に戻す
        double omega = Math.Log(Math.Max(distance, Math.E)) / (Damping * VisualDuration);
        return this with { VisualDuration = Math.Min(VisualDuration, VisualDurationOf(omega)) };
    }

    /// <summary>
    /// 距離（px）を動いて、見た目の時間ちょうどに残りが <paramref name="within"/> px を切るばね（動き終えた時刻に別の要素へ切り替えても跳ばないように）。
    /// 残りは、行き過ぎのないばね（ζ = 1）では (1 + ωt)·e^(−ωt)、行き過ぎるばね（ζ &lt; 1）では振幅の包絡 e^(−ζωt) ÷ √(1 − ζ²) で縮む
    /// （ω は Period の逆数）。<see cref="ForDistance"/> の目安の e^(−ζωt) では、見た目の時間にまだ数 px 手前にいる。
    /// </summary>
    public Spring Landing(double distance, double within = 0.5)
    {
        double zeta = Damping;
        double goal = within / Math.Max(distance, within);

        // x = ω × 見た目の時間。残りは x とともに減るので、二分法で求める
        double low = 0, high = 100;
        for (int i = 0; i < 50; i++)
        {
            double mid = (low + high) / 2;
            if (Remaining(zeta, mid) > goal)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        // 着き切るのはもとの見た目の時間とする
        return this with { VisualDuration = Math.Min(VisualDuration, VisualDurationOf(high / VisualDuration)) };
    }

    /// <summary>動き出しの距離を 1 としたときの、x = ωt の時点の残り（の上限）。</summary>
    internal static double Remaining(double zeta, double x) => zeta >= 1
        ? (1 + x) * Math.Exp(-x)
        : Math.Exp(-zeta * x) / Math.Sqrt(1 - zeta * zeta);

    /// <summary>固有角振動数から、<see cref="Period"/> と同じ換算（ω = 2π ÷ 見た目の時間 ÷ 1.2）で見た目の時間に戻す。</summary>
    private static double VisualDurationOf(double omega) => 2 * Math.PI / (1.2 * omega);
}
