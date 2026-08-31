using System.Collections.ObjectModel;
using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Reordering rules is part of the configuration, not a display preference — the list order is
/// the evaluation order — so the boundary cases matter: a failed move must be distinguishable
/// from a successful one, or the page would mark the configuration dirty for a no-op.
/// </summary>
public sealed class ListReorderTests
{
    private static ObservableCollection<string> List(params string[] items) => [.. items];

    [Fact]
    public void MoveUp_MiddleItem_MovesItOnePlaceTowardsTheStart()
    {
        var list = List("a", "b", "c");

        ListReorder.MoveUp(list, "b").Should().BeTrue();

        list.Should().Equal("b", "a", "c");
    }

    [Fact]
    public void MoveDown_MiddleItem_MovesItOnePlaceTowardsTheEnd()
    {
        var list = List("a", "b", "c");

        ListReorder.MoveDown(list, "b").Should().BeTrue();

        list.Should().Equal("a", "c", "b");
    }

    [Fact]
    public void MoveUp_FirstItem_DoesNothingAndReportsIt()
    {
        var list = List("a", "b");

        ListReorder.MoveUp(list, "a").Should().BeFalse();

        list.Should().Equal("a", "b");
    }

    [Fact]
    public void MoveDown_LastItem_DoesNothingAndReportsIt()
    {
        var list = List("a", "b");

        ListReorder.MoveDown(list, "b").Should().BeFalse();

        list.Should().Equal("a", "b");
    }

    [Fact]
    public void MoveUp_ItemNotInTheList_DoesNothing()
    {
        var list = List("a", "b");

        ListReorder.MoveUp(list, "z").Should().BeFalse();
        ListReorder.MoveDown(list, "z").Should().BeFalse();

        list.Should().Equal("a", "b");
    }

    [Fact]
    public void Move_SingleItemList_IsAlwaysANoOp()
    {
        var list = List("only");

        ListReorder.MoveUp(list, "only").Should().BeFalse();
        ListReorder.MoveDown(list, "only").Should().BeFalse();
    }

    [Fact]
    public void Move_EmptyList_DoesNotThrow()
    {
        var list = new ObservableCollection<string>();

        var act = () => ListReorder.MoveUp(list, "anything");

        act.Should().NotThrow();
        ListReorder.MoveDown(list, "anything").Should().BeFalse();
    }

    [Fact]
    public void MoveUp_ThenMoveDown_RestoresTheOriginalOrder()
    {
        var list = List("a", "b", "c");

        ListReorder.MoveUp(list, "c");
        ListReorder.MoveDown(list, "c");

        list.Should().Equal("a", "b", "c");
    }
}
