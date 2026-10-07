using Tasklabe.Core.Domain;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

public class DependencySchedulerTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    // 2026-09-14 (月) 〜 09-18 (金) は稼働日。09-19、20 は土日、09-21〜23 は祝日。09-24 (木) から稼働日
    private static readonly DateOnly Today = D(9, 1);

    private static TaskItem T(string id, string? parent = null, DateOnly? start = null, DateOnly? target = null,
        DateOnly? actualStart = null, double progress = 0, StatusCategory category = StatusCategory.Todo,
        TaskKind kind = TaskKind.Task, params string[] blockedBy) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        Start = start,
        Target = target,
        ActualStart = actualStart,
        ProgressPercent = progress,
        Category = category,
        Kind = kind,
        BlockedBy = blockedBy.Select(b => "I-" + b).ToList(),
    };

    private static Dictionary<string, PlanDates> Resolve(DateOnly today, params TaskItem[] tasks) =>
        DependencyScheduler.Resolve(TaskTree.Build(tasks), today).ToDictionary(s => s.Task.ItemId, s => s.After);

    // ---------------------------------------------------------------- 計画変更による調整

    [Fact]
    public void Parent_predecessor_ends_with_its_children_not_with_its_own_old_progress()
    {
        // p は 9/1 に着手して進捗 5 % のまま子 a を足した。p 自身の進捗からの見込みではなく、a の終わり（9/14）で後続を決める
        var shifts = Resolve(D(9, 7),
            T("p", start: D(9, 1), target: D(9, 14), actualStart: D(9, 1), progress: 5, category: StatusCategory.InProgress),
            T("a", parent: "p", start: D(9, 8), target: D(9, 14)),
            T("b", start: D(9, 15), target: D(9, 16), blockedBy: ["p"]));

        Assert.False(shifts.ContainsKey("b"));
    }

    [Fact]
    public void Successor_that_starts_before_the_predecessor_ends_moves_to_the_next_working_day()
    {
        // a は 9/14〜9/16。b は 9/15 から 2 稼働日 → 9/17〜9/18 へ
        var shifts = Resolve(Today,
            T("a", start: D(9, 14), target: D(9, 16)),
            T("b", start: D(9, 15), target: D(9, 16), blockedBy: ["a"]));

        Assert.Equal(new PlanDates(D(9, 17), D(9, 18)), shifts["b"]);
    }

    [Fact]
    public void Only_successors_starting_before_the_predecessor_ends_are_marked_as_overlapping()
    {
        // b は a の終わり（9/17）より前に始まっている。c は b のいまの終わり（9/16）の後に始まるので、b に押されて動くだけ
        var shifts = DependencyScheduler.Resolve(TaskTree.Build(
        [
            T("a", start: D(9, 14), target: D(9, 17)),
            T("b", start: D(9, 16), target: D(9, 16), blockedBy: ["a"]),
            T("c", start: D(9, 17), target: D(9, 17), blockedBy: ["b"]),
        ]), Today).ToDictionary(s => s.Task.ItemId);

        Assert.True(shifts["b"].Overlaps);
        Assert.False(shifts["c"].Overlaps);
    }

    [Fact]
    public void Canceled_predecessor_does_not_hold_back_successors()
    {
        var shifts = Resolve(Today,
            T("a", start: D(9, 14), target: D(9, 16), category: StatusCategory.Canceled),
            T("b", start: D(9, 15), target: D(9, 16), blockedBy: ["a"]));

        Assert.Empty(shifts);
    }

    [Fact]
    public void Duration_is_kept_in_working_days_across_holidays()
    {
        // a が 9/18 (金) に終わる。b (3 稼働日) は 9/24 (木) から始まり 9/28 (月) に終わる
        var shifts = Resolve(Today,
            T("a", start: D(9, 16), target: D(9, 18)),
            T("b", start: D(9, 17), target: D(9, 24), blockedBy: ["a"]));

        Assert.Equal(new PlanDates(D(9, 24), D(9, 28)), shifts["b"]);
    }

    [Fact]
    public void Shifts_propagate_along_a_chain()
    {
        var shifts = Resolve(Today,
            T("a", start: D(9, 14), target: D(9, 17)),
            T("b", start: D(9, 16), target: D(9, 16), blockedBy: ["a"]),
            T("c", start: D(9, 17), target: D(9, 17), blockedBy: ["b"]));

        Assert.Equal(new PlanDates(D(9, 18), D(9, 18)), shifts["b"]);
        Assert.Equal(new PlanDates(D(9, 24), D(9, 24)), shifts["c"]);
    }

    [Fact]
    public void Successors_that_already_start_later_are_not_pulled_forward()
    {
        var shifts = Resolve(Today,
            T("a", start: D(9, 14), target: D(9, 15)),
            T("b", start: D(9, 28), target: D(9, 29), blockedBy: ["a"]));

        Assert.Empty(shifts);
    }

    [Fact]
    public void Started_and_done_successors_are_not_moved()
    {
        var shifts = Resolve(Today,
            T("a", start: D(9, 14), target: D(9, 16)),
            T("started", start: D(9, 15), target: D(9, 16), actualStart: D(9, 15), blockedBy: ["a"]),
            T("done", start: D(9, 15), target: D(9, 15), category: StatusCategory.Done, blockedBy: ["a"]));

        Assert.Empty(shifts);
    }

    // ---------------------------------------------------------------- 親子

    [Fact]
    public void Parent_successor_moves_its_whole_subtree_keeping_the_internal_order()
    {
        // 設計 (9/14〜9/16) → 実装フェーズ。実装の中は 9/15〜 と 9/17〜 の 2 段。
        // 最も早い 9/15 を 9/17 に合わせ、全体を 2 稼働日ずらす
        var shifts = Resolve(Today,
            T("design", start: D(9, 14), target: D(9, 16)),
            T("impl", blockedBy: ["design"]),
            T("impl1", parent: "impl", start: D(9, 15), target: D(9, 16)),
            T("impl2", parent: "impl", start: D(9, 17), target: D(9, 17)));

        Assert.Equal(new PlanDates(D(9, 17), D(9, 18)), shifts["impl1"]);
        Assert.Equal(new PlanDates(D(9, 24), D(9, 24)), shifts["impl2"]);
    }

    [Fact]
    public void Parent_predecessor_ends_when_its_last_child_ends()
    {
        var shifts = Resolve(Today,
            T("phase"),
            T("p1", parent: "phase", start: D(9, 14), target: D(9, 14)),
            T("p2", parent: "phase", start: D(9, 15), target: D(9, 17)),
            T("next", start: D(9, 16), target: D(9, 16), blockedBy: ["phase"]));

        Assert.Equal(new PlanDates(D(9, 18), D(9, 18)), shifts["next"]);
    }

    // ---------------------------------------------------------------- 遅延による調整

    [Fact]
    public void Delayed_predecessor_pushes_successors_by_its_forecast()
    {
        // a は 9/14 に着手し、今日 9/16 で進捗 25 %。見込みは予定終了の 9/16 を越える
        var result = DependencyScheduler.Resolve(TaskTree.Build([
            T("a", start: D(9, 14), target: D(9, 16), actualStart: D(9, 14), progress: 25, category: StatusCategory.InProgress),
            T("b", start: D(9, 17), target: D(9, 17), blockedBy: ["a"]),
        ]), D(9, 16));

        var shift = Assert.Single(result);
        Assert.True(shift.CauseDelayed);
        Assert.Equal("a", shift.Cause.ItemId);
        Assert.True(shift.After.Start > D(9, 17));
    }

    [Fact]
    public void Unstarted_predecessor_past_its_start_is_assumed_to_start_today()
    {
        // a は 9/14〜9/15 の予定だが、9/17 になっても未着手。今日から 2 稼働日で 9/18 に終わる見込み
        var shifts = Resolve(D(9, 17),
            T("a", start: D(9, 14), target: D(9, 15)),
            T("b", start: D(9, 16), target: D(9, 16), blockedBy: ["a"]));

        Assert.Equal(new PlanDates(D(9, 24), D(9, 24)), shifts["b"]);
    }

    // ---------------------------------------------------------------- 利用者の指定

    [Fact]
    public void Pinned_dates_are_kept_and_used_for_successors()
    {
        var tree = TaskTree.Build([
            T("a", start: D(9, 14), target: D(9, 14)),
            T("b", start: D(9, 15), target: D(9, 15), blockedBy: ["a"]),
        ]);
        var pinned = new Dictionary<string, PlanDates> { ["a"] = new(D(9, 14), D(9, 16)) };

        var shift = Assert.Single(DependencyScheduler.Resolve(tree, Today, pinned));

        Assert.Equal("b", shift.Task.ItemId);
        Assert.Equal(new PlanDates(D(9, 17), D(9, 17)), shift.After);
    }

    [Fact]
    public void Pinned_successor_is_not_moved_even_if_it_overlaps()
    {
        var tree = TaskTree.Build([
            T("a", start: D(9, 14), target: D(9, 16)),
            T("b", start: D(9, 17), target: D(9, 17), blockedBy: ["a"]),
        ]);
        var pinned = new Dictionary<string, PlanDates> { ["b"] = new(D(9, 15), D(9, 15)) };

        Assert.Empty(DependencyScheduler.Resolve(tree, Today, pinned));
    }

    [Fact]
    public void Downstream_only_leaves_unrelated_conflicts_alone()
    {
        var tree = TaskTree.Build([
            T("a", start: D(9, 14), target: D(9, 14)),
            T("b", start: D(9, 15), target: D(9, 15), blockedBy: ["a"]),
            T("x", start: D(9, 14), target: D(9, 16)),
            T("y", start: D(9, 15), target: D(9, 15), blockedBy: ["x"]),
        ]);
        var pinned = new Dictionary<string, PlanDates> { ["a"] = new(D(9, 14), D(9, 16)) };

        var moved = DependencyScheduler.Resolve(tree, Today, pinned, pinned.Keys).Select(s => s.Task.ItemId);

        Assert.Equal(["b"], moved);
    }

    [Fact]
    public void Changing_a_child_reaches_the_successors_of_its_parent()
    {
        var tree = TaskTree.Build([
            T("phase"),
            T("c", parent: "phase", start: D(9, 14), target: D(9, 14)),
            T("next", start: D(9, 15), target: D(9, 15), blockedBy: ["phase"]),
        ]);
        var pinned = new Dictionary<string, PlanDates> { ["c"] = new(D(9, 14), D(9, 16)) };

        var shift = Assert.Single(DependencyScheduler.Resolve(tree, Today, pinned, pinned.Keys));

        Assert.Equal(new PlanDates(D(9, 17), D(9, 17)), shift.After);
    }

    [Fact]
    public void New_link_can_be_resolved_for_the_successors_of_the_predecessor_only()
    {
        var tree = TaskTree.Build([
            T("a", start: D(9, 14), target: D(9, 16)),
            T("b", start: D(9, 15), target: D(9, 15), blockedBy: ["a"]),
            T("x", start: D(9, 14), target: D(9, 16)),
            T("y", start: D(9, 15), target: D(9, 15), blockedBy: ["x"]),
        ]);

        var moved = DependencyScheduler.Resolve(tree, Today, downstreamOf: ["a"]).Select(s => s.Task.ItemId);

        Assert.Equal(["b"], moved);
    }

    // ---------------------------------------------------------------- 張れる組み合わせ

    [Fact]
    public void Links_between_a_parent_and_its_descendants_are_rejected()
    {
        var parent = T("phase");
        var child = T("c", parent: "phase");
        var tree = TaskTree.Build([parent, child]);

        Assert.Equal(LinkCheck.Hierarchy, DependencyScheduler.CanLink(tree, parent, child));
        Assert.Equal(LinkCheck.Hierarchy, DependencyScheduler.CanLink(tree, child, parent));
        Assert.Equal(LinkCheck.Same, DependencyScheduler.CanLink(tree, parent, parent));
    }

    [Fact]
    public void Cycles_along_a_chain_are_detected()
    {
        var a = T("a");
        var b = T("b", blockedBy: ["a"]);
        var c = T("c", blockedBy: ["b"]);
        var tree = TaskTree.Build([a, b, c]);

        Assert.Equal(LinkCheck.Cycle, DependencyScheduler.CanLink(tree, c, a));
        Assert.Equal(LinkCheck.Ok, DependencyScheduler.CanLink(tree, a, c));
    }

    [Fact]
    public void Cycles_through_a_parent_are_detected()
    {
        // phase → x がある。x → phase の子 は、phase の子が x より先でも後でもあることになり循環する
        var phase = T("phase");
        var child = T("c", parent: "phase");
        var x = T("x", blockedBy: ["phase"]);
        var tree = TaskTree.Build([phase, child, x]);

        Assert.Equal(LinkCheck.Cycle, DependencyScheduler.CanLink(tree, x, child));
        Assert.Equal(LinkCheck.AlreadyLinked, DependencyScheduler.CanLink(tree, phase, x));
    }

    [Fact]
    public void Links_between_independent_tasks_and_phases_are_allowed()
    {
        var design = T("design");
        var d1 = T("d1", parent: "design");
        var impl = T("impl");
        var i1 = T("i1", parent: "impl");
        var tree = TaskTree.Build([design, d1, impl, i1]);

        Assert.Equal(LinkCheck.Ok, DependencyScheduler.CanLink(tree, design, impl));
        Assert.Equal(LinkCheck.Ok, DependencyScheduler.CanLink(tree, d1, i1));
        Assert.Equal(LinkCheck.NotPlanned, DependencyScheduler.CanLink(tree, T("issue", kind: TaskKind.Issue), impl));
    }
}
