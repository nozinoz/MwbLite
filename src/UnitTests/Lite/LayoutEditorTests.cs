// MwbLite: tests for the layout editor's model (drag = MoveTile).
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite.Ui;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class LayoutEditorTests
{
    [TestMethod]
    public void MovingAPcSwapsWithTheTargetSlot()
    {
        var editor = new LayoutModel { Slots = new[] { "minipc", "laptop1", "", "laptop2" } };
        int changes = 0;
        editor.LayoutChanged += (s, e) => changes++;

        Assert.IsTrue(editor.MoveTile(1, 0));
        CollectionAssert.AreEqual(new[] { "LAPTOP1", "MINIPC", "", "LAPTOP2" }, editor.Slots);

        Assert.IsTrue(editor.MoveTile(3, 2), "move into an empty slot");
        CollectionAssert.AreEqual(new[] { "LAPTOP1", "MINIPC", "LAPTOP2", "" }, editor.Slots);

        Assert.IsFalse(editor.MoveTile(3, 0), "an empty slot can't be dragged");
        Assert.IsFalse(editor.MoveTile(1, 1));
        Assert.IsFalse(editor.MoveTile(0, 4));
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void AddAndRemoveNames()
    {
        var editor = new LayoutModel { Slots = new[] { "A", "", "C", "" } };

        Assert.IsTrue(editor.AddName("b"));
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "" }, editor.Slots);
        Assert.IsFalse(editor.AddName("a"), "already present");
        Assert.IsTrue(editor.AddName("D"));
        Assert.IsFalse(editor.AddName("E"), "all 4 slots used");

        editor.RemoveAt(1);
        CollectionAssert.AreEqual(new[] { "A", "", "C", "D" }, editor.Slots);
    }

    [TestMethod]
    public void NeighboursFollowOneRowAndGrid()
    {
        var editor = new LayoutModel { OneRow = true };
        Assert.AreEqual(1, editor.Neighbour(0, 1, 0));
        Assert.AreEqual(-1, editor.Neighbour(3, 1, 0));
        Assert.AreEqual(-1, editor.Neighbour(1, 0, 1), "one row has nothing below");

        editor.OneRow = false; // [0][1] / [2][3]
        Assert.AreEqual(2, editor.Neighbour(0, 0, 1));
        Assert.AreEqual(1, editor.Neighbour(0, 1, 0));
        Assert.AreEqual(-1, editor.Neighbour(1, 1, 0));
        Assert.AreEqual(1, editor.Neighbour(3, 0, -1));
    }
}
