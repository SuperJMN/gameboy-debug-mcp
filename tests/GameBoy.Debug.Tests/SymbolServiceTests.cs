using GameBoy.Debug.Core;
using GameBoy.Debug.Symbols;

namespace GameBoy.Debug.Tests;

public sealed class SymbolServiceTests
{
    [Fact]
    public void Loads_rgbds_style_symbols()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            "; comment",
            "00:0150 Start",
            "02:4000 Banked.Function",
            "C120 Player.X",
        ]);

        try
        {
            var service = new SymbolService();

            var loaded = service.Load(path);
            var start = service.Resolve("Start");
            var playerX = service.Resolve("Player.X");

            Assert.True(loaded.IsSuccess);
            Assert.Equal(3, loaded.Value);
            Assert.True(start.IsSuccess);
            Assert.Equal(0, start.Value.Bank);
            Assert.Equal(0x0150, start.Value.Address);
            Assert.True(playerX.IsSuccess);
            Assert.Null(playerX.Value.Bank);
            Assert.Equal(0xC120, playerX.Value.Address);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reports_missing_symbols()
    {
        var service = new SymbolService();

        var result = service.Resolve("Missing.Symbol");

        Assert.False(result.IsSuccess);
        Assert.Equal("symbol_not_found", result.Error?.Code);
    }
}
