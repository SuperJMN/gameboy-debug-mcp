using GameBoy.Debug.SameBoy;

namespace GameBoy.Debug.Tests;

public sealed class SameBoyDebugSessionIntegrationTests
{
    [Fact]
    public void SameBoy_backend_loads_and_controls_minimal_rom()
    {
        if (!NativeBridgeExists())
        {
            return;
        }

        var romPath = Path.Combine(Path.GetTempPath(), $"gameboy-debug-mcp-{Guid.NewGuid():N}.gb");
        var symPath = Path.ChangeExtension(romPath, ".sym");
        CreateMinimalRom(romPath);
        File.WriteAllLines(symPath, ["C000 Player.X"]);

        try
        {
            using var session = new SameBoyDebugSession();

            var loaded = session.LoadRom(romPath);
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);
            Assert.True(loaded.Value.Loaded);
            Assert.Equal("DMG", loaded.Value.Model);

            var reset = session.Reset();
            Assert.True(reset.IsSuccess, reset.Error?.Message);
            Assert.True(reset.Value.Reset);

            var registers = session.ReadRegisters();
            Assert.True(registers.IsSuccess, registers.Error?.Message);
            Assert.Equal("0x0100", registers.Value.Pc);

            var memory = session.ReadMemory(0x0100, 3);
            Assert.True(memory.IsSuccess, memory.Error?.Message);
            Assert.Equal("3E 2A EA", memory.Value.BytesHex);

            var disassembly = session.Disassemble(0x0100, 2);
            Assert.True(disassembly.IsSuccess, disassembly.Error?.Message);
            Assert.Contains(disassembly.Value.Instructions, instruction => instruction.Text.Contains("LD A", StringComparison.OrdinalIgnoreCase));

            var step = session.StepInstruction(1);
            Assert.True(step.IsSuccess, step.Error?.Message);
            Assert.Equal("0x0100", step.Value.PcBefore);
            Assert.Equal("0x0102", step.Value.PcAfter);

            var breakpoint = session.SetBreakpoint(0x0102, null);
            Assert.True(breakpoint.IsSuccess, breakpoint.Error?.Message);
            var continued = session.ContinueUntilBreak(16);
            Assert.True(continued.IsSuccess, continued.Error?.Message);
            Assert.Equal("breakpoint", continued.Value.Reason);

            var trace = session.TraceUntilWrite(0xC000, 16);
            Assert.True(trace.IsSuccess, trace.Error?.Message);
            Assert.Equal("write", trace.Value.Reason);
            Assert.Equal("0x2A", trace.Value.Value);

            var lastWriter = session.FindLastWriter(0xC000);
            Assert.True(lastWriter.IsSuccess, lastWriter.Error?.Message);
            Assert.True(lastWriter.Value.Found);
            Assert.Equal("0x2A", lastWriter.Value.Value);

            var written = session.WriteMemory(0xC000, [0x2A]);
            Assert.True(written.IsSuccess, written.Error?.Message);
            var symbols = session.LoadSymbols(symPath);
            Assert.True(symbols.IsSuccess, symbols.Error?.Message);
            var readSymbol = session.ReadSymbol("Player.X", 1);
            Assert.True(readSymbol.IsSuccess, readSymbol.Error?.Message);
            Assert.Equal("2A", readSymbol.Value.BytesHex);

            var oam = session.ReadOam();
            Assert.True(oam.IsSuccess, oam.Error?.Message);
            Assert.Equal(40, oam.Value.Sprites.Count);

            var ppu = session.ReadPpuState();
            Assert.True(ppu.IsSuccess, ppu.Error?.Message);
            Assert.StartsWith("0x", ppu.Value.Lcdc, StringComparison.Ordinal);

            var tilemap = session.DumpTilemap(0x9800);
            Assert.True(tilemap.IsSuccess, tilemap.Error?.Message);
            Assert.Equal(32, tilemap.Value.Rows.Count);

            var tileset = session.DumpTileset(0x8000, 2);
            Assert.True(tileset.IsSuccess, tileset.Error?.Message);
            Assert.Equal(2, tileset.Value.Tiles.Count);

            var screen = session.CaptureScreen();
            Assert.True(screen.IsSuccess, screen.Error?.Message);
            Assert.True(File.Exists(screen.Value.ImagePath));
            Assert.Equal(160, screen.Value.Width);
            Assert.Equal(144, screen.Value.Height);
        }
        finally
        {
            File.Delete(romPath);
            File.Delete(symPath);
        }
    }

    [Fact]
    public void Post_boot_lcd_state_allows_ly_wait_loops_to_advance()
    {
        if (!NativeBridgeExists())
        {
            return;
        }

        var romPath = Path.Combine(Path.GetTempPath(), $"gameboy-debug-mcp-ly-wait-{Guid.NewGuid():N}.gb");
        CreateLyWaitRom(romPath);

        try
        {
            using var session = new SameBoyDebugSession();

            var loaded = session.LoadRom(romPath);
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);

            var ppu = session.ReadPpuState();
            Assert.True(ppu.IsSuccess, ppu.Error?.Message);
            Assert.True(ppu.Value.LcdEnabled);

            var trace = session.TraceUntilWrite(0xC000, 200_000);
            Assert.True(trace.IsSuccess, trace.Error?.Message);
            Assert.Equal("write", trace.Value.Reason);
            Assert.Equal("0x2A", trace.Value.Value);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    private static bool NativeBridgeExists()
    {
        var root = FindRepoRoot();
        return root is not null && File.Exists(Path.Combine(root, "native", "out", "linux-x64", "libgameboy_debug_sameboy.so"));
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "gameboy-debug-mcp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void CreateMinimalRom(string path)
    {
        var rom = Enumerable.Repeat((byte)0x00, 0x8000).ToArray();
        rom[0x100] = 0x3E;
        rom[0x101] = 0x2A;
        rom[0x102] = 0xEA;
        rom[0x103] = 0x00;
        rom[0x104] = 0xC0;
        rom[0x105] = 0x18;
        rom[0x106] = 0xFE;
        var title = "MCPTEST"u8.ToArray();
        Array.Copy(title, 0, rom, 0x134, title.Length);
        rom[0x147] = 0x00;
        rom[0x148] = 0x00;
        rom[0x149] = 0x00;
        File.WriteAllBytes(path, rom);
    }

    private static void CreateLyWaitRom(string path)
    {
        var rom = Enumerable.Repeat((byte)0x00, 0x8000).ToArray();
        rom[0x100] = 0xF0; // LDH A, [$44]
        rom[0x101] = 0x44;
        rom[0x102] = 0xFE; // CP $90
        rom[0x103] = 0x90;
        rom[0x104] = 0x38; // JR C, $0100
        rom[0x105] = 0xFA;
        rom[0x106] = 0x3E; // LD A, $2A
        rom[0x107] = 0x2A;
        rom[0x108] = 0xEA; // LD [$C000], A
        rom[0x109] = 0x00;
        rom[0x10A] = 0xC0;
        rom[0x10B] = 0x18; // JR $010B
        rom[0x10C] = 0xFE;
        var title = "LYWAIT"u8.ToArray();
        Array.Copy(title, 0, rom, 0x134, title.Length);
        rom[0x147] = 0x00;
        rom[0x148] = 0x00;
        rom[0x149] = 0x00;
        File.WriteAllBytes(path, rom);
    }
}
