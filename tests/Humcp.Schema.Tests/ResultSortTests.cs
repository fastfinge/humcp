using Humcp.Schema;

namespace Humcp.Schema.Tests;

public class ResultSortTests
{
    private static ResultRow Row(int number, params string[] cells)
        => new(number, [.. cells.Select(c => new ResultCell(c, null))]);

    private static int[] Sorted(ResultSort sort, params ResultRow[] rows)
        => [.. rows.Order(sort).Select(r => r.Number)];

    [Fact]
    public void Numbers_sort_as_numbers()
        => Assert.Equal([2, 3, 1], Sorted(new ResultSort(0, ascending: true), Row(1, "10"), Row(2, "9"), Row(3, "9.5")));

    [Fact]
    public void Text_sorts_without_regard_to_case()
        => Assert.Equal([2, 1, 3], Sorted(new ResultSort(0, ascending: true), Row(1, "beta"), Row(2, "Alpha"), Row(3, "gamma")));

    [Fact]
    public void Empty_cells_go_last_in_both_directions()
    {
        ResultRow[] rows = [Row(1, ""), Row(2, "5"), Row(3, "7")];

        Assert.Equal([2, 3, 1], Sorted(new ResultSort(0, ascending: true), rows));
        Assert.Equal([3, 2, 1], Sorted(new ResultSort(0, ascending: false), rows));
    }

    [Fact]
    public void Equal_values_keep_their_row_order()
        => Assert.Equal([1, 2, 3], Sorted(new ResultSort(0, ascending: false), Row(1, "x"), Row(2, "x"), Row(3, "x")));

    [Fact]
    public void Sorts_by_the_column_asked_for()
        => Assert.Equal([2, 1], Sorted(new ResultSort(1, ascending: true), Row(1, "a", "z"), Row(2, "b", "y")));

    [Fact]
    public void Says_what_it_did_and_keeps_saying_it_in_the_header()
    {
        Assert.Equal("Sorted by Name, descending.", ResultSort.Describe("Name", ascending: false));
        Assert.Equal("Name, sorted ascending", ResultSort.Header("Name", true));
        Assert.Equal("Name", ResultSort.Header("Name", null));
    }
}
