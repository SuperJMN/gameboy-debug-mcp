using GameBoy.Debug.Core;

namespace GameBoy.Debug.Tests;

public sealed class BreakpointCollectionTests
{
    [Fact]
    public void Adds_and_clears_breakpoints_by_id()
    {
        var breakpoints = new BreakpointCollection();

        var first = breakpoints.Set(0x0150, null);
        var second = breakpoints.Set(0xC000, "a == 1");

        Assert.Equal("bp-1", first.Id);
        Assert.Equal("0x0150", first.Address);
        Assert.True(first.Enabled);
        Assert.True(breakpoints.Contains(0x0150));
        Assert.True(breakpoints.Contains(0xC000));

        var cleared = breakpoints.Clear(first.Id);

        Assert.True(cleared);
        Assert.False(breakpoints.Contains(0x0150));
        Assert.True(breakpoints.Contains(0xC000));
        Assert.Equal("bp-2", second.Id);
    }
}
