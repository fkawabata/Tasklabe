using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Tasklabe.Animation;

/// <summary>
/// 画面の動き。Fluent 2 のモーションに合わせ、出るときは減速、消えるときは加速で短く動かし、位置や大きさはばねで動かす。
/// Windows の「アニメーション効果」がオフのときは動かさず、その場で値を変える。
/// </summary>
public static class Motion
{
    private static readonly UISettings Settings = new();

    /// <summary>Windows の設定でアニメーションが有効か。</summary>
    public static bool IsEnabled => Settings.AnimationsEnabled;

    // ---------------------------------------------------------------- ばね

    /// <summary>選択の印が項目のあいだを滑る（並べた切り替え）。約 0.4 秒で落ち着き、止まる手前でわずかに行き過ぎる。</summary>
    public static readonly Spring Morph = new(0.42, 0.16);

    /// <summary>ボタンがパネルに育つ。</summary>
    public static readonly Spring Grow = new(0.48, 0.12);

    /// <summary>パネルがボタンに戻る。行き過ぎない。</summary>
    public static readonly Spring Shrink = new(0.38, 0);

    /// <summary>範囲の帯が伸び縮みする。</summary>
    public static readonly Spring Stretch = new(0.3, 0.04);

    /// <summary>小さな印が項目から項目へ移る。</summary>
    public static readonly Spring Glide = new(0.3, 0.1);

    /// <summary>ページ（暦の月など）をめくる。</summary>
    public static readonly Spring Slide = new(0.4, 0.06);

    /// <summary>ポインターの動きに遅れずに印が移る。ドラッグした面が戻る。</summary>
    public static readonly Spring Snappy = new(0.26, 0.12);

    /// <summary>面が端から出てくる。行き過ぎない。</summary>
    public static readonly Spring Smooth = new(0.4, 0);

    // ---------------------------------------------------------------- 加減速

    /// <summary>出てくる要素の減速。</summary>
    public static CompositionEasingFunction EnterEasing(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

    /// <summary>消える要素・位置を変える要素の標準の加減速。</summary>
    public static CompositionEasingFunction StandardEasing(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    // ---------------------------------------------------------------- 要素

    /// <summary>Translation を動かせる形で、要素の Visual を返す。</summary>
    public static Visual VisualOf(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    /// <summary>
    /// ずらした位置から薄い状態で現す（出てくる要素の減速で）。
    /// </summary>
    /// <param name="from">現れ始める位置（並べた位置からのずれ）。</param>
    /// <param name="travel">位置をばねで動かすときのばね。null なら不透明度と同じ時間で動かす。</param>
    public static void Reveal(UIElement element, Vector3 from, TimeSpan duration, TimeSpan delay = default, Spring? travel = null)
    {
        var visual = VisualOf(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;
        visual.Properties.InsertVector3("Translation", from);

        var easing = EnterEasing(visual.Compositor);
        EaseTo(visual, "Opacity", 1f, duration, easing, delay);
        if (travel is { } spring)
        {
            SpringTo(visual, "Translation", Vector3.Zero, spring);
        }
        else
        {
            EaseTo(visual, "Translation", Vector3.Zero, duration, easing, delay);
        }
    }

    /// <summary>ずらした位置へ動かしながら薄れさせ、消え終えたら <paramref name="completed"/> を呼ぶ（要素を取り除くなど）。</summary>
    /// <param name="to">消え終える位置（並べた位置からのずれ）。</param>
    public static void Conceal(UIElement element, Vector3 to, TimeSpan duration, Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!IsEnabled)
        {
            completed();
            return;
        }

        var visual = VisualOf(element);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        EaseTo(visual, "Opacity", 0f, duration);
        EaseTo(visual, "Translation", to, duration);
        batch.End();
        batch.Completed += (_, _) => completed();
    }

    // ---------------------------------------------------------------- ばねで動かす

    /// <summary>ばねで値を動かす。アニメーションが無効なとき、<paramref name="animate"/> が false のときは、その場で値を変える。</summary>
    public static void SpringTo(CompositionObject target, string property, float value, Spring spring, bool animate = true) =>
        Start(target, property, value, animate, compositor =>
        {
            var animation = compositor.CreateSpringScalarAnimation();
            animation.FinalValue = value;
            animation.DampingRatio = spring.Damping;
            animation.Period = spring.Period;
            return animation;
        });

    /// <inheritdoc cref="SpringTo(CompositionObject, string, float, Spring, bool)"/>
    public static void SpringTo(CompositionObject target, string property, Vector2 value, Spring spring, bool animate = true) =>
        Start(target, property, value, animate, compositor =>
        {
            var animation = compositor.CreateSpringVector2Animation();
            animation.FinalValue = value;
            animation.DampingRatio = spring.Damping;
            animation.Period = spring.Period;
            return animation;
        });

    /// <inheritdoc cref="SpringTo(CompositionObject, string, float, Spring, bool)"/>
    /// <param name="velocity">動き始めの速さ（px/秒）。ドラッグを離したときの速さを引き継ぐ。</param>
    public static void SpringTo(CompositionObject target, string property, Vector3 value, Spring spring,
        Vector3 velocity = default, bool animate = true) =>
        Start(target, property, value, animate, compositor =>
        {
            var animation = compositor.CreateSpringVector3Animation();
            animation.FinalValue = value;
            animation.InitialVelocity = velocity;
            animation.DampingRatio = spring.Damping;
            animation.Period = spring.Period;
            return animation;
        });

    // ---------------------------------------------------------------- 決まった時間で動かす

    /// <summary>
    /// 決まった時間で値を動かす（不透明度など）。アニメーションが無効なとき、時間が 0 のときは、その場で値を変える。
    /// </summary>
    /// <param name="easing">加減速。null なら <see cref="StandardEasing"/>。</param>
    public static void EaseTo(CompositionObject target, string property, float value, TimeSpan duration,
        CompositionEasingFunction? easing = null, TimeSpan delay = default) =>
        Ease(target, property, value, duration, easing, delay, (compositor, ease) =>
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1, value, ease);
            return animation;
        });

    /// <inheritdoc cref="EaseTo(CompositionObject, string, float, TimeSpan, CompositionEasingFunction?, TimeSpan)"/>
    public static void EaseTo(CompositionObject target, string property, Vector2 value, TimeSpan duration,
        CompositionEasingFunction? easing = null, TimeSpan delay = default) =>
        Ease(target, property, value, duration, easing, delay, (compositor, ease) =>
        {
            var animation = compositor.CreateVector2KeyFrameAnimation();
            animation.InsertKeyFrame(1, value, ease);
            return animation;
        });

    /// <inheritdoc cref="EaseTo(CompositionObject, string, float, TimeSpan, CompositionEasingFunction?, TimeSpan)"/>
    public static void EaseTo(CompositionObject target, string property, Vector3 value, TimeSpan duration,
        CompositionEasingFunction? easing = null, TimeSpan delay = default) =>
        Ease(target, property, value, duration, easing, delay, (compositor, ease) =>
        {
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1, value, ease);
            return animation;
        });

    /// <summary>決まった時間で色を移す。アニメーションが無効なとき、時間が 0 のときは、その場で色を変える。</summary>
    public static void EaseTo(CompositionColorBrush target, Windows.UI.Color value, TimeSpan duration,
        CompositionEasingFunction? easing = null, TimeSpan delay = default) =>
        Ease(target, "Color", value, duration, easing, delay, (compositor, ease) =>
        {
            var animation = compositor.CreateColorKeyFrameAnimation();
            animation.InsertKeyFrame(1, value, ease);
            return animation;
        });

    // ---------------------------------------------------------------- 共通

    /// <summary>いまの値から始まるキーフレームのアニメーションで動かす。待つあいだも、いまの値のまま置いておく。</summary>
    private static void Ease(CompositionObject target, string property, object value, TimeSpan duration,
        CompositionEasingFunction? easing, TimeSpan delay, Func<Compositor, CompositionEasingFunction, KeyFrameAnimation> create) =>
        Start(target, property, value, duration > TimeSpan.Zero, compositor =>
        {
            var animation = create(compositor, easing ?? StandardEasing(compositor));
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
            animation.Duration = duration;
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            return animation;
        });

    /// <summary>動かせるならアニメーションを始め、動かさないなら動いている途中のものを止めてその場に置く。</summary>
    private static void Start(CompositionObject target, string property, object value, bool animate,
        Func<Compositor, CompositionAnimation> create)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!animate || !IsEnabled)
        {
            target.StopAnimation(property);
            SetDirect(target, property, value);
            return;
        }

        target.StartAnimation(property, create(target.Compositor));
    }

    /// <summary>アニメーションなしで値を置く。Translation は Visual の拡張プロパティなので Properties に入れる。</summary>
    private static void SetDirect(CompositionObject target, string property, object value)
    {
        switch (target, property, value)
        {
            case (Visual v, "Opacity", float f):
                v.Opacity = f;
                break;
            case (DropShadow d, "Opacity", float f):
                d.Opacity = f;
                break;
            case (Visual v, "RotationAngleInDegrees", float r):
                v.RotationAngleInDegrees = r;
                break;
            case (Visual v, "Offset", Vector3 o):
                v.Offset = o;
                break;
            case (Visual v, "Scale", Vector3 s):
                v.Scale = s;
                break;
            case (Visual v, "Translation", Vector3 t):
                v.Properties.InsertVector3("Translation", t);
                break;
            case (CompositionRoundedRectangleGeometry g, "Offset", Vector2 o):
                g.Offset = o;
                break;
            case (CompositionRoundedRectangleGeometry g, "Size", Vector2 s):
                g.Size = s;
                break;
            case (CompositionRoundedRectangleGeometry g, "CornerRadius", Vector2 r):
                g.CornerRadius = r;
                break;
            case (CompositionShape s, "Offset", Vector2 o):
                s.Offset = o;
                break;
            case (CompositionShape s, "Scale", Vector2 sc):
                s.Scale = sc;
                break;
            case (CompositionColorBrush b, "Color", Windows.UI.Color c):
                b.Color = c;
                break;
            default:
                throw new ArgumentException($"{target.GetType().Name} の {property} には、アニメーションなしで値を置けません。", nameof(property));
        }
    }
}
