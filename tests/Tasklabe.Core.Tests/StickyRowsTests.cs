using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>縦にスクロールしたときに上端に残す親の行。</summary>
public class StickyRowsTests
{
    private const double H = 10;

    // 0 A
    // 1   A1
    // 2     A1a
    // 3     A1b
    // 4   A2
    // 5 B
    // 6   B1
    private static readonly int[] Depths = [0, 1, 2, 2, 1, 0, 1];

    [Fact]
    public void Nothing_is_kept_while_the_parent_is_still_in_its_place()
    {
        Assert.Empty(StickyRows.Compute(Depths, 0, H, 5));
    }

    [Fact]
    public void A_parent_partly_scrolled_off_is_kept_at_the_top()
    {
        // A が欠けて重なると、その下に隠れかけた A1 も配下が続く親なので重ねる
        var rows = StickyRows.Compute(Depths, 3, H, 5);

        Assert.Equal([new StickyRow(0, 0), new StickyRow(1, 10)], rows);
    }

    [Fact]
    public void Parents_of_the_first_row_below_the_kept_rows_are_stacked_from_the_outside()
    {
        // 上端は A1a。A を重ねると、その下から見え始めるのは A1b で、その親は A1
        var rows = StickyRows.Compute(Depths, 20, H, 5);

        Assert.Equal([new StickyRow(0, 0), new StickyRow(1, 10)], rows);
        Assert.Equal(20, StickyRows.Covered(rows, H));
    }

    [Fact]
    public void The_kept_row_is_pushed_up_when_its_last_child_leaves()
    {
        // A1 の配下は A1b（30〜40）まで。上端が 25 のとき、A の下の A1 は 40 − 25 − 10 = 5 まで押し上げる
        var rows = StickyRows.Compute(Depths, 25, H, 5);

        Assert.Equal([new StickyRow(0, 0), new StickyRow(1, 5)], rows);
    }

    [Fact]
    public void The_next_parent_replaces_the_previous_one_after_its_subtree_ends()
    {
        // 上端が 47 のとき A の配下（〜50）は抜けかけ、その下から B（50〜）が見え始める
        var pushed = StickyRows.Compute(Depths, 47, H, 5);
        Assert.Equal([new StickyRow(0, -7)], pushed);

        var next = StickyRows.Compute(Depths, 52, H, 5);
        Assert.Equal([new StickyRow(5, 0)], next);
    }

    [Fact]
    public void The_count_is_limited_from_the_outside()
    {
        var rows = StickyRows.Compute(Depths, 20, H, 1);

        Assert.Equal([new StickyRow(0, 0)], rows);
    }

    [Fact]
    public void A_leaf_at_the_top_is_not_kept()
    {
        // 最上位の葉（B1 の後に何もない）だけが並ぶとき
        Assert.Empty(StickyRows.Compute([0, 0, 0], 5, H, 5));
    }

    [Fact]
    public void Hit_testing_prefers_the_outer_row()
    {
        var rows = new[] { new StickyRow(0, 0), new StickyRow(1, 3) };

        Assert.Equal(0, StickyRows.At(rows, 5, H));
        Assert.Equal(1, StickyRows.At(rows, 12, H));
        Assert.Null(StickyRows.At(rows, 13, H));
    }

    [Fact]
    public void Revealing_a_row_leaves_room_for_its_parents()
    {
        // A1b（30〜40）を見せるには、A と A1 の 2 行分を上に空ける
        double scroll = StickyRows.RevealTop(Depths, 3, H, 5);

        Assert.Equal(10, scroll);
        Assert.True(scroll + StickyRows.Covered(StickyRows.Compute(Depths, scroll, H, 5), H) <= 30);
    }

    [Fact]
    public void Revealing_a_row_right_below_its_parent_scrolls_the_parent_into_its_place()
    {
        // A1（10〜20）は、A がそのまま上に見えている位置で見える
        Assert.Equal(0, StickyRows.RevealTop(Depths, 1, H, 5));
    }
}
