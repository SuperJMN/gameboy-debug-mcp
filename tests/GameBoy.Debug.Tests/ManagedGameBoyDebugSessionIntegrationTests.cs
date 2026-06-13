using System;
using System.IO;
using System.Linq;
using GameBoy.Debug.Core;
using GameBoy.Debug.Emulator;

namespace GameBoy.Debug.Tests;

public sealed class ManagedGameBoyDebugSessionIntegrationTests
{
    [Fact]
    public void State_reports_no_rom_before_loading()
    {
        using var session = new ManagedGameBoyDebugSession();

        var state = session.GetState();

        Assert.True(state.IsSuccess, state.Error?.Message);
        Assert.False(state.Value.RomLoaded);
        Assert.Null(state.Value.Title);
    }

    [Fact]
    public void Reads_before_loading_fail_cleanly()
    {
        using var session = new ManagedGameBoyDebugSession();

        var registers = session.ReadRegisters();

        Assert.False(registers.IsSuccess);
        Assert.Equal("no_rom_loaded", registers.Error?.Code);
    }

    [Fact]
    public void Managed_backend_loads_and_controls_minimal_rom()
    {
        var romPath = CreateTestFilePath("managed-mcp", ".gb");
        var symPath = Path.ChangeExtension(romPath, ".sym");
        CreateMinimalRom(romPath);
        File.WriteAllLines(symPath, ["C000 Player.X"]);

        try
        {
            using var session = new ManagedGameBoyDebugSession();

            var loaded = session.LoadRom(romPath);
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);
            Assert.Equal("MCPTEST", loaded.Value.RomTitle);
            Assert.Equal("DMG", loaded.Value.Model);

            var state = session.GetState();
            Assert.True(state.Value.RomLoaded);
            Assert.Equal("0x0100", state.Value.Pc);

            var registers = session.ReadRegisters();
            Assert.True(registers.IsSuccess, registers.Error?.Message);
            Assert.Equal("0x0100", registers.Value.Pc);

            var memory = session.ReadMemory(0x0100, 3);
            Assert.True(memory.IsSuccess, memory.Error?.Message);
            Assert.Equal("3E 2A EA", memory.Value.BytesHex);

            var disassembly = session.Disassemble(0x0100, 2);
            Assert.True(disassembly.IsSuccess, disassembly.Error?.Message);
            Assert.Contains(disassembly.Value.Instructions, i => i.Text.Contains("LD A", StringComparison.OrdinalIgnoreCase));

            var step = session.StepInstruction(1);
            Assert.True(step.IsSuccess, step.Error?.Message);
            Assert.Equal("0x0100", step.Value.PcBefore);
            Assert.Equal("0x0102", step.Value.PcAfter);

            var breakpoint = session.SetBreakpoint(0x0102, null);
            Assert.True(breakpoint.IsSuccess, breakpoint.Error?.Message);
            var listed = session.ListBreakpoints();
            Assert.Single(listed.Value.Breakpoints);

            var reset = session.Reset();
            Assert.True(reset.IsSuccess, reset.Error?.Message);
            Assert.Single(session.ListBreakpoints().Value.Breakpoints); // breakpoints survive reset

            var continued = session.ContinueUntilBreak(16);
            Assert.True(continued.IsSuccess, continued.Error?.Message);
            Assert.Equal("breakpoint", continued.Value.Reason);
            Assert.Equal("0x0102", continued.Value.Pc);

            var trace = session.TraceUntilWrite(0xC000, 16);
            Assert.True(trace.IsSuccess, trace.Error?.Message);
            Assert.Equal("write", trace.Value.Reason);
            Assert.Equal("0x2A", trace.Value.Value);

            var lastWriter = session.FindLastWriter(0xC000);
            Assert.True(lastWriter.Value.Found);
            Assert.Equal("0x2A", lastWriter.Value.Value);

            var symbols = session.LoadSymbols(symPath);
            Assert.True(symbols.IsSuccess, symbols.Error?.Message);
            var readSymbol = session.ReadSymbol("Player.X", 1);
            Assert.True(readSymbol.IsSuccess, readSymbol.Error?.Message);
            Assert.Equal("2A", readSymbol.Value.BytesHex);

            var written = session.WriteMemory(0xC000, [0x5A]);
            Assert.True(written.IsSuccess, written.Error?.Message);
            Assert.Equal("5A", session.ReadMemory(0xC000, 1).Value.BytesHex);

            var oam = session.ReadOam();
            Assert.Equal(40, oam.Value.Sprites.Count);

            var ppu = session.ReadPpuState();
            Assert.True(ppu.IsSuccess, ppu.Error?.Message);
            Assert.StartsWith("0x", ppu.Value.Lcdc, StringComparison.Ordinal);

            var tilemap = session.DumpTilemap(0x9800);
            Assert.Equal(32, tilemap.Value.Rows.Count);

            var tileset = session.DumpTileset(0x8000, 2);
            Assert.Equal(2, tileset.Value.Tiles.Count);

            var screen = session.CaptureScreen();
            Assert.True(screen.IsSuccess, screen.Error?.Message);
            Assert.Equal(160, screen.Value.Width);
            Assert.Equal(144, screen.Value.Height);
            Assert.Equal("image/png", screen.Value.MimeType);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], screen.Value.Data[..4]);
        }
        finally
        {
            File.Delete(romPath);
            File.Delete(symPath);
        }
    }

    [Fact]
    public void Savestate_roundtrip_restores_registers_and_memory()
    {
        var romPath = CreateTestFilePath("managed-savestate", ".gb");
        var statePath = Path.ChangeExtension(romPath, ".s0");
        CreateMinimalRom(romPath);

        try
        {
            using var session = new ManagedGameBoyDebugSession();
            Assert.True(session.LoadRom(romPath).IsSuccess);
            session.WriteMemory(0xC000, Enumerable.Repeat((byte)0xA5, 16).ToArray());

            var registersBefore = session.ReadRegisters();
            var memoryBefore = session.ReadMemory(0xC000, 16);

            var saved = session.SaveState(statePath);
            Assert.True(saved.IsSuccess, saved.Error?.Message);
            Assert.True(File.Exists(statePath));

            session.WriteMemory(0xC000, Enumerable.Repeat((byte)0x5A, 16).ToArray());
            session.StepInstruction(1);
            Assert.NotEqual(memoryBefore.Value.BytesHex, session.ReadMemory(0xC000, 16).Value.BytesHex);

            var loadedState = session.LoadState(statePath);
            Assert.True(loadedState.IsSuccess, loadedState.Error?.Message);

            Assert.Equal(registersBefore.Value, session.ReadRegisters().Value);
            Assert.Equal(memoryBefore.Value.BytesHex, session.ReadMemory(0xC000, 16).Value.BytesHex);
        }
        finally
        {
            File.Delete(romPath);
            File.Delete(statePath);
        }
    }

    private static string CreateTestFilePath(string prefix, string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gameboy-mcp-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}{extension}");
    }

    private static void CreateMinimalRom(string path)
    {
        var rom = Enumerable.Repeat((byte)0x00, 0x8000).ToArray();
        rom[0x100] = 0x3E; // LD A, 0x2A
        rom[0x101] = 0x2A;
        rom[0x102] = 0xEA; // LD [0xC000], A
        rom[0x103] = 0x00;
        rom[0x104] = 0xC0;
        rom[0x105] = 0x18; // JR -2
        rom[0x106] = 0xFE;
        var title = "MCPTEST"u8.ToArray();
        Array.Copy(title, 0, rom, 0x134, title.Length);
        rom[0x147] = 0x00;
        rom[0x148] = 0x00;
        rom[0x149] = 0x00;
        File.WriteAllBytes(path, rom);
    }
}
