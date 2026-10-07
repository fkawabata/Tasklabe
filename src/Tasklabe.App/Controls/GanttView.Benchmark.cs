#if DEBUG
using Microsoft.UI.Xaml.Media;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;
#endif

namespace Tasklabe.App.Controls;

/// <summary>
/// ガントチャートの描画の性能の計測（要件 NF-03）。開発時の検証用のため、Debug ビルドにだけ含める。
/// 環境変数 TASKLABE_GANTT_BENCHMARK=1 で起動すると、ガントを開いたときに計測してログに書く。
/// </summary>
public sealed partial class GanttView
{
#if DEBUG
    private bool BenchmarkRunning => _benchmark is not null;

    private void StartBenchmarkIfRequested()
    {
        if (Environment.GetEnvironmentVariable("TASKLABE_GANTT_BENCHMARK") == "1")
        {
            RunBenchmark();
        }
    }

    private void RecordFrame(long started) => _benchmark?.Record(started);

    private Benchmark? _benchmark;

    /// <summary>描画の間隔と所要時間の記録。</summary>
    private sealed class Benchmark
    {
        public List<double> Intervals { get; } = [];

        public List<double> DrawTimes { get; } = [];

        /// <summary>スクロールに伴う左側の行の配置にかかった時間。</summary>
        public List<double> UpdateTimes { get; } = [];

        private long _last;

        public void Reset()
        {
            Intervals.Clear();
            DrawTimes.Clear();
            UpdateTimes.Clear();
            _last = 0;
        }

        public void Record(long started)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started, now).TotalMilliseconds);
            if (_last != 0)
            {
                Intervals.Add(System.Diagnostics.Stopwatch.GetElapsedTime(_last, started).TotalMilliseconds);
            }

            _last = started;
        }
    }

    /// <summary>
    /// 1,000 行の架空のタスクを表示し、毎フレーム縦横にスクロールしたときの描画の間隔を記録する。
    /// 環境変数 TASKLABE_GANTT_BENCHMARK=1 のときだけ動かす（開発時の検証用）。
    /// </summary>
    private void RunBenchmark()
    {
        var start = Today.AddDays(-60);
        var tasks = new List<TaskItem>();
        for (int p = 0; p < 100; p++)
        {
            var parentIssue = $"bench-p{p}";
            tasks.Add(new TaskItem { ItemId = parentIssue, ProjectId = "bench", IssueId = parentIssue, RepositoryNameWithOwner = "bench/bench", Number = p * 10, Title = $"フェーズ {p}", SortOrder = p });
            for (int c = 0; c < 9; c++)
            {
                var s0 = start.AddDays(p + c * 2);
                tasks.Add(new TaskItem
                {
                    ItemId = $"bench-{p}-{c}",
                    ProjectId = "bench",
                    IssueId = $"bench-{p}-{c}",
                    RepositoryNameWithOwner = "bench/bench",
                    Number = p * 10 + c + 1,
                    Title = $"タスク {p}-{c}",
                    ParentIssueId = parentIssue,
                    SortOrder = c,
                    Kind = TaskKind.Task,
                    Start = s0,
                    Target = s0.AddDays(4),
                    ActualStart = c % 3 == 0 ? s0.AddDays(1) : null,
                    EstimateHours = 8,
                    ProgressPercent = c * 10,
                    BlockedBy = c > 0 ? [$"bench-{p}-{c - 1}"] : [],
                });
            }
        }

        LoadCore(new Project { Id = "bench", Number = 0, Title = "bench", Kind = ProjectKind.Personal, OwnerLogin = "bench" }, TaskTree.Build(tasks), null);
        var bench = _benchmark = new Benchmark();
        System.Diagnostics.Stopwatch? clock = null;
        void OnRendering(object? sender, object e)
        {
            if (clock is null && (Chart.ActualWidth <= 0 || bench.DrawTimes.Count == 0))
            {
                bench.Reset();
                return;
            }

            clock ??= System.Diagnostics.Stopwatch.StartNew();
            if (clock.Elapsed.TotalSeconds < 6)
            {
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                ScrollTo(_scrollX + 2, _scrollY >= ContentHeight - BodyHeight - 1 ? 0 : _scrollY + 24);
                bench.UpdateTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                return;
            }

            CompositionTarget.Rendering -= OnRendering;
            var b = bench;
            _benchmark = null;
            var intervals = b.Intervals.Order().ToList();
            var draws = b.DrawTimes.Order().ToList();
            var updates = b.UpdateTimes.Order().ToList();
            double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min((int)(v.Count * q), v.Count - 1)];
            Services.AppLog.Info(FormattableString.Invariant(
                $"ガントの描画計測（{_rows.Count} 行）: フレーム {b.DrawTimes.Count}、間隔 中央値 {P(intervals, 0.5):0.0}ms / 95% {P(intervals, 0.95):0.0}ms / 最大 {P(intervals, 1):0.0}ms、描画 中央値 {P(draws, 0.5):0.00}ms / 95% {P(draws, 0.95):0.00}ms / 最大 {P(draws, 1):0.00}ms、行の配置 中央値 {P(updates, 0.5):0.00}ms / 95% {P(updates, 0.95):0.00}ms / 最大 {P(updates, 1):0.00}ms"));
        }

        CompositionTarget.Rendering += OnRendering;
    }
#else
    private static bool BenchmarkRunning => false;

    private static void StartBenchmarkIfRequested()
    {
    }

    private static void RecordFrame(long started)
    {
        _ = started;
    }
#endif
}
