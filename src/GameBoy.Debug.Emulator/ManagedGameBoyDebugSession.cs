using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreBoy;
using CoreBoy.controller;
using CoreBoy.cpu;
using CoreBoy.memory.cart;
using CoreBoy.serial;
using CoreBoy.sound;
using GameBoy.Debug.Core;
using GameBoy.Debug.Symbols;

namespace GameBoy.Debug.Emulator
{
    /// <summary>
    /// Pure-managed <see cref="IGameBoyDebugSession"/> backed by the vendored CoreBoy emulator core.
    /// No native dependencies; runs anywhere .NET runs.
    /// </summary>
    public sealed class ManagedGameBoyDebugSession : IGameBoyDebugSession, IDisposable
    {
        private const int ScreenWidth = FrameBufferDisplay.Width;
        private const int ScreenHeight = FrameBufferDisplay.Height;
        private const int MaxStepCycles = 1_000_000;
        private const int CyclesPerFrame = 70224;
        private const uint StateMagic = 0x31534D47; // "GMS1"

        private static readonly (int Start, int Length)[] StateRegions =
        {
            (0x8000, 0x2000), // VRAM
            (0xA000, 0x2000), // cartridge RAM
            (0xC000, 0x2000), // WRAM
            (0xFE00, 0x00A0), // OAM
            (0xFF00, 0x0080), // I/O registers
            (0xFF80, 0x007F), // HRAM
            (0xFFFF, 0x0001), // interrupt enable
        };

        private static readonly IReadOnlyDictionary<JoypadButton, Button> ButtonMap =
            new Dictionary<JoypadButton, Button>
            {
                [JoypadButton.Right] = Button.Right,
                [JoypadButton.Left] = Button.Left,
                [JoypadButton.Up] = Button.Up,
                [JoypadButton.Down] = Button.Down,
                [JoypadButton.A] = Button.A,
                [JoypadButton.B] = Button.B,
                [JoypadButton.Select] = Button.Select,
                [JoypadButton.Start] = Button.Start,
            };

        private static readonly JoypadButton[] CanonicalButtons =
        {
            JoypadButton.Right, JoypadButton.Left, JoypadButton.Up, JoypadButton.Down,
            JoypadButton.A, JoypadButton.B, JoypadButton.Select, JoypadButton.Start,
        };

        private readonly BreakpointCollection breakpoints = new();
        private readonly WatchpointCollection watchpoints = new();
        private readonly SymbolService symbols = new();
        private readonly Dictionary<int, WriteRecord> lastWriters = new();

        private Gameboy gameboy;
        private FrameBufferDisplay display;
        private HeadlessController controller;
        private CoreBoy.gpu.Gpu.Mode? lastMode;
        private string romTitle;
        private string romModel;
        private bool romLoaded;
        private bool trackWrites;
        private bool trackReads;
        private WatchHit? watchHit;
        private bool disposed;

        public DebugResult<LoadRomResult> LoadRom(string path)
        {
            if (disposed)
            {
                return DebugResult<LoadRomResult>.Failure("session_disposed", "The debug session has been disposed.");
            }

            if (!File.Exists(path))
            {
                return DebugResult<LoadRomResult>.Failure("rom_not_found", $"ROM was not found: {path}");
            }

            try
            {
                BuildMachine(path);
                breakpoints.ClearAll();
                watchpoints.ClearAll();
                return DebugResult<LoadRomResult>.Success(new LoadRomResult(true, romTitle, romModel));
            }
            catch (Exception ex)
            {
                romLoaded = false;
                return DebugResult<LoadRomResult>.Failure("load_rom_failed", ex.Message);
            }
        }

        private void BuildMachine(string path)
        {
            var options = new GameboyOptions { Rom = path, DisableBatterySaves = true };
            var cartridge = new Cartridge(options);
            display = new FrameBufferDisplay();
            controller = new HeadlessController();
            gameboy = new Gameboy(options, cartridge, display, controller, new NullSoundOutput(), new NullSerialEndpoint());
            gameboy.Mmu.WriteObserver = OnMemoryWrite;
            gameboy.Mmu.ReadObserver = null;
            lastMode = null;
            lastWriters.Clear();
            trackWrites = true;
            trackReads = false;
            watchHit = null;
            romPath = path;
            romTitle = cartridge.Title;
            romModel = cartridge.Gbc ? "CGB" : "DMG";
            romLoaded = true;
        }

        public DebugResult<ResetResult> Reset()
        {
            if (!romLoaded)
            {
                return NoRom<ResetResult>();
            }

            try
            {
                BuildMachine(romPath);
                return DebugResult<ResetResult>.Success(new ResetResult(true));
            }
            catch (Exception ex)
            {
                return DebugResult<ResetResult>.Failure("reset_failed", ex.Message);
            }
        }

        public DebugResult<StepInstructionResult> StepInstruction(int count)
        {
            if (!romLoaded)
            {
                return NoRom<StepInstructionResult>();
            }

            var before = ReadRegisters();
            if (!before.IsSuccess)
            {
                return DebugResult<StepInstructionResult>.Failure(before.Error!.Code, before.Error.Message);
            }

            var disassembly = Disassemble(ParseWord(before.Value.Pc), Math.Min(count, 16));
            for (var i = 0; i < count; i++)
            {
                StepOnce();
            }

            var after = ReadRegisters();
            if (!after.IsSuccess)
            {
                return DebugResult<StepInstructionResult>.Failure(after.Error!.Code, after.Error.Message);
            }

            var text = disassembly.IsSuccess
                ? string.Join('\n', disassembly.Value.Instructions.Select(instruction => $"{instruction.Address}: {instruction.Text}"))
                : "";

            return DebugResult<StepInstructionResult>.Success(new StepInstructionResult(before.Value.Pc, after.Value.Pc, after.Value, text));
        }

        public DebugResult<RunFrameResult> RunFrame(int count)
        {
            if (!romLoaded)
            {
                return NoRom<RunFrameResult>();
            }

            watchHit = null;
            var framesRun = 0;
            AttachReadObserverIfNeeded();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var completed = RunSingleFrame();
                    if (completed)
                    {
                        framesRun++;
                    }

                    if (watchHit.HasValue)
                    {
                        break;
                    }
                }
            }
            finally
            {
                DetachReadObserver();
            }

            var registers = ReadRegisters();
            if (!registers.IsSuccess)
            {
                return DebugResult<RunFrameResult>.Failure(registers.Error!.Code, registers.Error.Message);
            }

            var hit = IsBreakpointHit(ParseWord(registers.Value.Pc), registers.Value);
            if (!hit.IsSuccess)
            {
                return DebugResult<RunFrameResult>.Failure(hit.Error!.Code, hit.Error.Message);
            }

            return DebugResult<RunFrameResult>.Success(new RunFrameResult(framesRun, registers.Value, hit.Value));
        }

        public DebugResult<JoypadStateResult> SetJoypad(IReadOnlyList<JoypadButton> pressedButtons)
        {
            if (!romLoaded)
            {
                return NoRom<JoypadStateResult>();
            }

            var mask = ToButtonMask(pressedButtons);
            if (!mask.IsSuccess)
            {
                return DebugResult<JoypadStateResult>.Failure(mask.Error!.Code, mask.Error.Message);
            }

            controller.SetPressed(pressedButtons.Select(button => ButtonMap[button]));
            return DebugResult<JoypadStateResult>.Success(ToJoypadState(mask.Value));
        }

        public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<JoypadButton> pressedButtons, int frameCount)
        {
            var pressed = SetJoypad(pressedButtons);
            if (!pressed.IsSuccess)
            {
                return DebugResult<PressButtonsResult>.Failure(pressed.Error!.Code, pressed.Error.Message);
            }

            var run = RunFrame(frameCount);
            var released = SetJoypad(Array.Empty<JoypadButton>());
            if (!run.IsSuccess)
            {
                return DebugResult<PressButtonsResult>.Failure(run.Error!.Code, run.Error.Message);
            }

            if (!released.IsSuccess)
            {
                return DebugResult<PressButtonsResult>.Failure(released.Error!.Code, released.Error.Message);
            }

            return DebugResult<PressButtonsResult>.Success(new PressButtonsResult(run.Value.FramesRun, released.Value, run.Value.Registers));
        }

        public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions)
        {
            if (!romLoaded)
            {
                return NoRom<ContinueResult>();
            }

            watchHit = null;
            AttachReadObserverIfNeeded();
            try
            {
                for (var i = 0; i < maxInstructions; i++)
                {
                    var cpu = gameboy.Cpu;
                    var pc = (ushort)cpu.Registers.PC;

                    // Fast path: only materialize the full register set when we actually need it
                    // (a breakpoint sits at this PC, or we are about to stop). This avoids formatting
                    // ~16 hex strings on every single instruction.
                    if (breakpoints.HasBreakpointAt(pc))
                    {
                        var registers = ReadRegisters();
                        if (!registers.IsSuccess)
                        {
                            return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                        }

                        var hit = IsBreakpointHit(pc, registers.Value);
                        if (!hit.IsSuccess)
                        {
                            return DebugResult<ContinueResult>.Failure(hit.Error!.Code, hit.Error.Message);
                        }

                        if (hit.Value)
                        {
                            return Stop("breakpoint", registers.Value);
                        }
                    }

                    if (cpu.State == State.HALTED || cpu.State == State.STOPPED)
                    {
                        var halted = ReadRegisters();
                        return halted.IsSuccess
                            ? Stop("halt", halted.Value)
                            : DebugResult<ContinueResult>.Failure(halted.Error!.Code, halted.Error.Message);
                    }

                    StepOnce();
                    if (watchHit.HasValue)
                    {
                        var registers = ReadRegisters();
                        return registers.IsSuccess
                            ? Stop("watchpoint", registers.Value)
                            : DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                    }
                }

                var final = ReadRegisters();
                return final.IsSuccess
                    ? Stop("maxInstructions", final.Value)
                    : DebugResult<ContinueResult>.Failure(final.Error!.Code, final.Error.Message);
            }
            finally
            {
                DetachReadObserver();
            }

            static DebugResult<ContinueResult> Stop(string reason, CpuRegisters registers) =>
                DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Pc, registers));
        }

        public DebugResult<ContinueResult> StepOver(int maxInstructions)
        {
            if (!romLoaded)
            {
                return NoRom<ContinueResult>();
            }

            var before = ReadRegisters();
            if (!before.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(before.Error!.Code, before.Error.Message);
            }

            var pc = ParseWord(before.Value.Pc);
            var opcode = ReadByte(pc);
            if (!IsCallOrRst(opcode, out var length))
            {
                return StepSingle("step");
            }

            var returnAddress = (ushort)(pc + length);
            var startSp = ParseWord(before.Value.Sp);
            return StepUntil(
                maxInstructions,
                registers => ParseWord(registers.Pc) == returnAddress && ParseWord(registers.Sp) >= startSp,
                "step_over");
        }

        public DebugResult<ContinueResult> StepOut(int maxInstructions)
        {
            if (!romLoaded)
            {
                return NoRom<ContinueResult>();
            }

            var before = ReadRegisters();
            if (!before.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(before.Error!.Code, before.Error.Message);
            }

            var startSp = ParseWord(before.Value.Sp);
            return StepUntil(maxInstructions, registers => ParseWord(registers.Sp) > startSp, "step_out");
        }

        public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
        {
            if (!BreakpointCondition.TryParse(condition, out var parsedCondition, out var conditionError))
            {
                return DebugResult<BreakpointSetResult>.Failure(
                    "invalid_breakpoint_condition",
                    $"Invalid breakpoint condition: {conditionError}");
            }

            var breakpoint = breakpoints.Set(address, condition, parsedCondition);
            return DebugResult<BreakpointSetResult>.Success(new BreakpointSetResult(breakpoint.Id, breakpoint.Address, breakpoint.Enabled));
        }

        public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId)
        {
            return breakpoints.Clear(breakpointId)
                ? DebugResult<ClearBreakpointResult>.Success(new ClearBreakpointResult(true))
                : DebugResult<ClearBreakpointResult>.Failure("breakpoint_not_found", $"Breakpoint '{breakpointId}' was not found.");
        }

        public DebugResult<ListBreakpointsResult> ListBreakpoints()
        {
            var entries = breakpoints.All
                .Select(breakpoint => new BreakpointEntry(breakpoint.Id, breakpoint.Address, breakpoint.Enabled, breakpoint.Condition))
                .ToArray();

            return DebugResult<ListBreakpointsResult>.Success(new ListBreakpointsResult(entries));
        }

        public DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode)
        {
            var watchpoint = watchpoints.Set(address, mode);
            return DebugResult<WatchpointSetResult>.Success(
                new WatchpointSetResult(watchpoint.Id, watchpoint.Address, ToWatchpointModeName(watchpoint.Mode), watchpoint.Enabled));
        }

        public DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId)
        {
            return watchpoints.Clear(watchpointId)
                ? DebugResult<ClearWatchpointResult>.Success(new ClearWatchpointResult(true))
                : DebugResult<ClearWatchpointResult>.Failure("watchpoint_not_found", $"Watchpoint '{watchpointId}' was not found.");
        }

        public DebugResult<ListWatchpointsResult> ListWatchpoints()
        {
            var entries = watchpoints.All
                .Select(watchpoint => new WatchpointEntry(watchpoint.Id, watchpoint.Address, ToWatchpointModeName(watchpoint.Mode), watchpoint.Enabled))
                .ToArray();

            return DebugResult<ListWatchpointsResult>.Success(new ListWatchpointsResult(entries));
        }

        public DebugResult<SessionStateResult> GetState()
        {
            if (!romLoaded)
            {
                return DebugResult<SessionStateResult>.Success(new SessionStateResult(false, null, null, false, null));
            }

            var registers = ReadRegisters();
            return registers.IsSuccess
                ? DebugResult<SessionStateResult>.Success(
                    new SessionStateResult(true, romTitle, romModel, registers.Value.Halted, registers.Value.Pc))
                : DebugResult<SessionStateResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        public DebugResult<CpuRegisters> ReadRegisters()
        {
            if (!romLoaded)
            {
                return NoRom<CpuRegisters>();
            }

            var r = gameboy.Cpu.Registers;
            return DebugResult<CpuRegisters>.Success(new CpuRegisters(
                Hex.FormatWord((ushort)r.AF),
                Hex.FormatWord((ushort)r.BC),
                Hex.FormatWord((ushort)r.DE),
                Hex.FormatWord((ushort)r.HL),
                Hex.FormatWord((ushort)r.SP),
                Hex.FormatWord((ushort)r.PC),
                Hex.FormatByte((byte)r.A),
                Hex.FormatByte((byte)r.Flags.FlagsByte),
                Hex.FormatByte((byte)r.B),
                Hex.FormatByte((byte)r.C),
                Hex.FormatByte((byte)r.D),
                Hex.FormatByte((byte)r.E),
                Hex.FormatByte((byte)r.H),
                Hex.FormatByte((byte)r.L),
                gameboy.Cpu.InterruptMasterEnabled,
                gameboy.Cpu.State == State.HALTED || gameboy.Cpu.State == State.STOPPED));
        }

        public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
        {
            if (!romLoaded)
            {
                return NoRom<MemoryReadResult>();
            }

            return DebugResult<MemoryReadResult>.Success(MemoryFormatter.Format(address, ReadBytes(address, length)));
        }

        public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes)
        {
            if (!romLoaded)
            {
                return NoRom<WriteMemoryResult>();
            }

            trackWrites = false;
            try
            {
                for (var i = 0; i < bytes.Count; i++)
                {
                    gameboy.Mmu.SetByte((address + i) & 0xFFFF, bytes[i]);
                }
            }
            finally
            {
                trackWrites = true;
            }

            return DebugResult<WriteMemoryResult>.Success(new WriteMemoryResult(true, Hex.FormatWord(address), bytes.Count));
        }

        public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount)
        {
            if (!romLoaded)
            {
                return NoRom<DisassembleResult>();
            }

            var instructions = Disassembler.Disassemble(address, instructionCount, ReadByte, symbols.ResolveAddress);
            return DebugResult<DisassembleResult>.Success(new DisassembleResult(Hex.FormatWord(address), instructions));
        }

        public DebugResult<OamDumpResult> ReadOam()
        {
            if (!romLoaded)
            {
                return NoRom<OamDumpResult>();
            }

            var oam = ReadBytes(0xFE00, 0xA0);
            var sprites = Enumerable.Range(0, 40)
                .Select(index =>
                {
                    var offset = index * 4;
                    var y = oam[offset];
                    var x = oam[offset + 1];
                    return new OamSprite(index, y, x, Hex.FormatByte(oam[offset + 2]), Hex.FormatByte(oam[offset + 3]), y is > 0 and < 160 && x is > 0 and < 168);
                })
                .ToArray();

            return DebugResult<OamDumpResult>.Success(new OamDumpResult(sprites));
        }

        public DebugResult<PpuStateResult> ReadPpuState()
        {
            if (!romLoaded)
            {
                return NoRom<PpuStateResult>();
            }

            var lcdc = ReadByte(0xFF40);
            var stat = ReadByte(0xFF41);
            var scy = ReadByte(0xFF42);
            var scx = ReadByte(0xFF43);
            var ly = ReadByte(0xFF44);
            var lyc = ReadByte(0xFF45);
            var bgp = ReadByte(0xFF47);
            var obp0 = ReadByte(0xFF48);
            var obp1 = ReadByte(0xFF49);
            var wy = ReadByte(0xFF4A);
            var wx = ReadByte(0xFF4B);
            var vbk = ReadByte(0xFF4F);

            return DebugResult<PpuStateResult>.Success(new PpuStateResult(
                Hex.FormatByte(lcdc),
                Hex.FormatByte(stat),
                stat & 0x03,
                Hex.FormatByte(ly),
                Hex.FormatByte(lyc),
                Hex.FormatByte(scy),
                Hex.FormatByte(scx),
                Hex.FormatByte(wy),
                Hex.FormatByte(wx),
                Hex.FormatByte(bgp),
                Hex.FormatByte(obp0),
                Hex.FormatByte(obp1),
                Hex.FormatByte(vbk),
                (lcdc & 0x80) != 0,
                (lcdc & 0x02) != 0,
                (lcdc & 0x20) != 0,
                (lcdc & 0x01) != 0));
        }

        public DebugResult<ScreenCaptureResult> CaptureScreen()
        {
            if (!romLoaded)
            {
                return NoRom<ScreenCaptureResult>();
            }

            var pixels = display.Snapshot();
            var data = PngEncoder.EncodeRgb24(pixels, ScreenWidth, ScreenHeight);
            return DebugResult<ScreenCaptureResult>.Success(new ScreenCaptureResult(ScreenWidth, ScreenHeight, "image/png", data));
        }

        public DebugResult<LastWriterResult> FindLastWriter(ushort address)
        {
            if (!romLoaded)
            {
                return NoRom<LastWriterResult>();
            }

            return lastWriters.TryGetValue(address, out var record)
                ? DebugResult<LastWriterResult>.Success(new LastWriterResult(true, Hex.FormatWord(address), Hex.FormatWord(record.Pc), Hex.FormatByte(record.Value), record.Count))
                : DebugResult<LastWriterResult>.Success(new LastWriterResult(false, Hex.FormatWord(address), null, null, 0));
        }

        public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions)
        {
            if (!romLoaded)
            {
                return NoRom<TraceUntilWriteResult>();
            }

            traceAddress = address;
            traceHit = false;
            uint instructionsRun = 0;
            try
            {
                for (var i = 0; i < maxInstructions && !traceHit; i++)
                {
                    StepOnce();
                    instructionsRun++;
                }
            }
            finally
            {
                traceAddress = -1;
            }

            var registers = ReadRegisters();
            if (!registers.IsSuccess)
            {
                return DebugResult<TraceUntilWriteResult>.Failure(registers.Error!.Code, registers.Error.Message);
            }

            return DebugResult<TraceUntilWriteResult>.Success(traceHit
                ? new TraceUntilWriteResult(true, "write", Hex.FormatWord(address), Hex.FormatWord(traceHitPc), Hex.FormatByte(traceHitValue), instructionsRun, registers.Value)
                : new TraceUntilWriteResult(true, "maxInstructions", Hex.FormatWord(address), null, null, instructionsRun, registers.Value));
        }

        public DebugResult<TilemapDumpResult> DumpTilemap(ushort address)
        {
            if (!romLoaded)
            {
                return NoRom<TilemapDumpResult>();
            }

            var bytes = ReadBytes(address, 32 * 32);
            var rows = Enumerable.Range(0, 32)
                .Select(row => Hex.FormatBytes(bytes.Skip(row * 32).Take(32)))
                .ToArray();

            return DebugResult<TilemapDumpResult>.Success(new TilemapDumpResult(Hex.FormatWord(address), 32, 32, rows));
        }

        public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount)
        {
            if (!romLoaded)
            {
                return NoRom<TilesetDumpResult>();
            }

            var bytes = ReadBytes(address, tileCount * 16);
            var tiles = Enumerable.Range(0, tileCount)
                .Select(index => new TileDump(index, Hex.FormatWord((ushort)(address + index * 16)), Hex.FormatBytes(bytes.Skip(index * 16).Take(16))))
                .ToArray();

            return DebugResult<TilesetDumpResult>.Success(new TilesetDumpResult(Hex.FormatWord(address), tileCount, tiles));
        }

        public DebugResult<LoadSymbolsResult> LoadSymbols(string path)
        {
            var loaded = symbols.Load(path);
            return loaded.IsSuccess
                ? DebugResult<LoadSymbolsResult>.Success(new LoadSymbolsResult(true, loaded.Value))
                : DebugResult<LoadSymbolsResult>.Failure(loaded.Error!.Code, loaded.Error.Message);
        }

        public DebugResult<ResolveSymbolResult> ResolveSymbol(string name)
        {
            var resolved = symbols.Resolve(name);
            return resolved.IsSuccess
                ? DebugResult<ResolveSymbolResult>.Success(new ResolveSymbolResult(name, Hex.FormatWord(resolved.Value.Address), resolved.Value.Bank))
                : DebugResult<ResolveSymbolResult>.Failure(resolved.Error!.Code, resolved.Error.Message);
        }

        public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length)
        {
            var resolved = symbols.Resolve(name);
            if (!resolved.IsSuccess)
            {
                return DebugResult<ReadSymbolResult>.Failure(resolved.Error!.Code, resolved.Error.Message);
            }

            if (!romLoaded)
            {
                return NoRom<ReadSymbolResult>();
            }

            var bytes = ReadBytes(resolved.Value.Address, length ?? 1);
            return DebugResult<ReadSymbolResult>.Success(new ReadSymbolResult(name, Hex.FormatWord(resolved.Value.Address), bytes, Hex.FormatBytes(bytes)));
        }

        public DebugResult<SaveStateResult> SaveState(string path)
        {
            if (!romLoaded)
            {
                return NoRom<SaveStateResult>();
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(stream);
                writer.Write(StateMagic);

                var r = gameboy.Cpu.Registers;
                writer.Write((byte)r.A);
                writer.Write((byte)r.B);
                writer.Write((byte)r.C);
                writer.Write((byte)r.D);
                writer.Write((byte)r.E);
                writer.Write((byte)r.H);
                writer.Write((byte)r.L);
                writer.Write((byte)r.Flags.FlagsByte);
                writer.Write((ushort)r.SP);
                writer.Write((ushort)r.PC);

                foreach (var (start, length) in StateRegions)
                {
                    for (var i = 0; i < length; i++)
                    {
                        writer.Write(ReadByte((ushort)(start + i)));
                    }
                }

                return DebugResult<SaveStateResult>.Success(new SaveStateResult(true, path));
            }
            catch (Exception ex)
            {
                return DebugResult<SaveStateResult>.Failure("save_state_failed", ex.Message);
            }
        }

        public DebugResult<LoadStateResult> LoadState(string path)
        {
            if (!romLoaded)
            {
                return NoRom<LoadStateResult>();
            }

            if (!File.Exists(path))
            {
                return DebugResult<LoadStateResult>.Failure("state_not_found", $"Save state was not found: {path}");
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != StateMagic)
                {
                    return DebugResult<LoadStateResult>.Failure("invalid_state", "The file is not a managed Game Boy save state.");
                }

                var a = reader.ReadByte();
                var b = reader.ReadByte();
                var c = reader.ReadByte();
                var d = reader.ReadByte();
                var e = reader.ReadByte();
                var h = reader.ReadByte();
                var l = reader.ReadByte();
                var f = reader.ReadByte();
                var sp = reader.ReadUInt16();
                var pc = reader.ReadUInt16();

                trackWrites = false;
                try
                {
                    foreach (var (start, length) in StateRegions)
                    {
                        for (var i = 0; i < length; i++)
                        {
                            gameboy.Mmu.SetByte((start + i) & 0xFFFF, reader.ReadByte());
                        }
                    }
                }
                finally
                {
                    trackWrites = true;
                }

                var r = gameboy.Cpu.Registers;
                r.SetAf((a << 8) | f);
                r.SetBc((b << 8) | c);
                r.SetDe((d << 8) | e);
                r.SetHl((h << 8) | l);
                r.SP = sp;
                r.PC = pc;

                return DebugResult<LoadStateResult>.Success(new LoadStateResult(true, path));
            }
            catch (Exception ex)
            {
                return DebugResult<LoadStateResult>.Failure("load_state_failed", ex.Message);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            romLoaded = false;
            gameboy = null;
            disposed = true;
        }

        private int traceAddress = -1;
        private bool traceHit;
        private ushort traceHitPc;
        private byte traceHitValue;
        private string romPath;

        private void OnMemoryWrite(int address, int value)
        {
            if (!trackWrites)
            {
                return;
            }

            var pc = (ushort)gameboy.Cpu.Registers.PC;
            var masked = address & 0xFFFF;
            var byteValue = (byte)(value & 0xFF);
            lastWriters[masked] = lastWriters.TryGetValue(masked, out var existing)
                ? new WriteRecord(pc, byteValue, existing.Count + 1)
                : new WriteRecord(pc, byteValue, 1);

            if (masked == traceAddress)
            {
                traceHit = true;
                traceHitPc = pc;
                traceHitValue = byteValue;
            }

            if (watchpoints.TryMatch((ushort)masked, isWrite: true, out var watchpoint))
            {
                watchHit = new WatchHit((ushort)masked, watchpoint.Mode, pc, byteValue);
            }
        }

        private void OnMemoryRead(int address)
        {
            if (!trackReads)
            {
                return;
            }

            var masked = (ushort)(address & 0xFFFF);
            if (watchpoints.TryMatch(masked, isWrite: false, out var watchpoint))
            {
                watchHit = new WatchHit(masked, watchpoint.Mode, (ushort)gameboy.Cpu.Registers.PC, null);
            }
        }

        private void StepOnce()
        {
            var previousTrackReads = trackReads;
            trackReads = gameboy.Mmu.ReadObserver != null;
            try
            {
                var cpu = gameboy.Cpu;
                var guard = 0;

                // When halted/stopped, advance (bounded) until an interrupt wakes the CPU.
                if (cpu.State == State.HALTED || cpu.State == State.STOPPED)
                {
                    do
                    {
                        TickOnce();
                        guard++;
                    }
                    while ((cpu.State == State.HALTED || cpu.State == State.STOPPED) && guard < MaxStepCycles);
                    return;
                }

                // Cpu.Tick() is clock-divided (4 ticks per machine cycle). An instruction always leaves
                // State.OPCODE during decode and returns to State.OPCODE once it retires. Tick until the
                // opcode is consumed, then until the next fetch boundary.
                var leftOpcode = false;
                while (guard < MaxStepCycles)
                {
                    TickOnce();
                    guard++;

                    var state = cpu.State;
                    if (state == State.HALTED || state == State.STOPPED)
                    {
                        return;
                    }

                    if (!leftOpcode)
                    {
                        if (state != State.OPCODE)
                        {
                            leftOpcode = true;
                        }
                    }
                    else if (state == State.OPCODE)
                    {
                        return;
                    }
                }
            }
            finally
            {
                trackReads = previousTrackReads;
            }
        }

        private bool RunSingleFrame()
        {
            var previousTrackReads = trackReads;
            trackReads = gameboy.Mmu.ReadObserver != null;
            try
            {
                for (var cycle = 0; cycle < CyclesPerFrame; cycle++)
                {
                    TickOnce();
                    if (watchHit.HasValue)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                trackReads = previousTrackReads;
            }
        }

        private void TickOnce()
        {
            var mode = gameboy.Tick();
            if (mode.HasValue)
            {
                if (mode.Value == CoreBoy.gpu.Gpu.Mode.VBlank && lastMode != CoreBoy.gpu.Gpu.Mode.VBlank)
                {
                    display.RequestRefresh();
                }

                lastMode = mode;
            }
        }

        private byte ReadByte(ushort address) => (byte)(gameboy.Mmu.GetByte(address) & 0xFF);

        private byte[] ReadBytes(ushort address, int length)
        {
            var buffer = new byte[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = ReadByte((ushort)((address + i) & 0xFFFF));
            }

            return buffer;
        }

        private DebugResult<bool> IsBreakpointHit(ushort address, CpuRegisters registers)
        {
            foreach (var breakpoint in breakpoints.FindAll(address))
            {
                var shouldBreak = ShouldBreak(breakpoint, registers);
                if (!shouldBreak.IsSuccess)
                {
                    return shouldBreak;
                }

                if (shouldBreak.Value)
                {
                    return DebugResult<bool>.Success(true);
                }
            }

            return DebugResult<bool>.Success(false);
        }

        private DebugResult<bool> ShouldBreak(BreakpointInfo breakpoint, CpuRegisters registers)
        {
            if (breakpoint.ParsedCondition is null)
            {
                return string.IsNullOrWhiteSpace(breakpoint.Condition)
                    ? DebugResult<bool>.Success(true)
                    : DebugResult<bool>.Failure("invalid_breakpoint_condition", $"Breakpoint '{breakpoint.Id}' has an invalid condition.");
            }

            return breakpoint.ParsedCondition.Evaluate(new ConditionContext(this, registers));
        }

        private DebugResult<ContinueResult> StepSingle(string reason)
        {
            watchHit = null;
            AttachReadObserverIfNeeded();
            try
            {
                StepOnce();
                var registers = ReadRegisters();
                if (!registers.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                }

                if (watchHit.HasValue)
                {
                    return Stop("watchpoint", registers.Value);
                }

                var breakpoint = IsBreakpointHit(ParseWord(registers.Value.Pc), registers.Value);
                if (!breakpoint.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                }

                if (breakpoint.Value)
                {
                    return Stop("breakpoint", registers.Value);
                }

                return registers.Value.Halted ? Stop("halt", registers.Value) : Stop(reason, registers.Value);
            }
            finally
            {
                DetachReadObserver();
            }
        }

        private DebugResult<ContinueResult> StepUntil(int maxInstructions, Func<CpuRegisters, bool> completed, string completedReason)
        {
            watchHit = null;
            AttachReadObserverIfNeeded();
            try
            {
                for (var i = 0; i < maxInstructions; i++)
                {
                    if (gameboy.Cpu.State == State.HALTED || gameboy.Cpu.State == State.STOPPED)
                    {
                        var halted = ReadRegisters();
                        return halted.IsSuccess
                            ? Stop("halt", halted.Value)
                            : DebugResult<ContinueResult>.Failure(halted.Error!.Code, halted.Error.Message);
                    }

                    StepOnce();
                    var registers = ReadRegisters();
                    if (!registers.IsSuccess)
                    {
                        return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                    }

                    if (watchHit.HasValue)
                    {
                        return Stop("watchpoint", registers.Value);
                    }

                    var breakpoint = IsBreakpointHit(ParseWord(registers.Value.Pc), registers.Value);
                    if (!breakpoint.IsSuccess)
                    {
                        return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                    }

                    if (breakpoint.Value)
                    {
                        return Stop("breakpoint", registers.Value);
                    }

                    if (registers.Value.Halted)
                    {
                        return Stop("halt", registers.Value);
                    }

                    if (completed(registers.Value))
                    {
                        return Stop(completedReason, registers.Value);
                    }
                }

                var final = ReadRegisters();
                return final.IsSuccess
                    ? Stop("maxInstructions", final.Value)
                    : DebugResult<ContinueResult>.Failure(final.Error!.Code, final.Error.Message);
            }
            finally
            {
                DetachReadObserver();
            }
        }

        private void AttachReadObserverIfNeeded()
        {
            trackReads = false;
            if (watchpoints.HasEnabledReadWatchpoints)
            {
                gameboy.Mmu.ReadObserver = OnMemoryRead;
            }
        }

        private void DetachReadObserver()
        {
            if (gameboy != null)
            {
                gameboy.Mmu.ReadObserver = null;
            }

            trackReads = false;
        }

        private static DebugResult<ContinueResult> Stop(string reason, CpuRegisters registers) =>
            DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Pc, registers));

        private static bool IsCallOrRst(byte opcode, out int length)
        {
            length = opcode is 0xCD or 0xC4 or 0xCC or 0xD4 or 0xDC ? 3 : 1;
            return opcode is 0xCD or 0xC4 or 0xCC or 0xD4 or 0xDC
                or 0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF;
        }

        private static string ToWatchpointModeName(WatchpointMode mode) => mode switch
        {
            WatchpointMode.Read => "read",
            WatchpointMode.Write => "write",
            WatchpointMode.Access => "access",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        private static DebugResult<byte> ToButtonMask(IReadOnlyList<JoypadButton> pressedButtons)
        {
            byte mask = 0;
            foreach (var button in pressedButtons)
            {
                if ((int)button is < 0 or > 7)
                {
                    return DebugResult<byte>.Failure("invalid_button", $"Unsupported joypad button value: {(int)button}.");
                }

                mask |= (byte)(1 << (int)button);
            }

            return DebugResult<byte>.Success(mask);
        }

        private static JoypadStateResult ToJoypadState(byte mask)
        {
            return new JoypadStateResult(
                IsPressed(mask, JoypadButton.Right),
                IsPressed(mask, JoypadButton.Left),
                IsPressed(mask, JoypadButton.Up),
                IsPressed(mask, JoypadButton.Down),
                IsPressed(mask, JoypadButton.A),
                IsPressed(mask, JoypadButton.B),
                IsPressed(mask, JoypadButton.Select),
                IsPressed(mask, JoypadButton.Start),
                CanonicalButtons.Where(button => IsPressed(mask, button)).Select(ToButtonName).ToArray());
        }

        private static bool IsPressed(byte mask, JoypadButton button) => (mask & (1 << (int)button)) != 0;

        private static string ToButtonName(JoypadButton button) => button switch
        {
            JoypadButton.Right => "right",
            JoypadButton.Left => "left",
            JoypadButton.Up => "up",
            JoypadButton.Down => "down",
            JoypadButton.A => "a",
            JoypadButton.B => "b",
            JoypadButton.Select => "select",
            JoypadButton.Start => "start",
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
        };

        private static ushort ParseWord(string value) =>
            (ushort)Convert.ToInt32(value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), 16);

        private static DebugResult<T> NoRom<T>() =>
            DebugResult<T>.Failure("no_rom_loaded", "No ROM has been loaded into the session.");

        private readonly struct WriteRecord
        {
            public WriteRecord(ushort pc, byte value, ulong count)
            {
                Pc = pc;
                Value = value;
                Count = count;
            }

            public ushort Pc { get; }

            public byte Value { get; }

            public ulong Count { get; }
        }

        private readonly struct WatchHit
        {
            public WatchHit(ushort address, WatchpointMode mode, ushort pc, byte? value)
            {
                Address = address;
                Mode = mode;
                Pc = pc;
                Value = value;
            }

            public ushort Address { get; }

            public WatchpointMode Mode { get; }

            public ushort Pc { get; }

            public byte? Value { get; }
        }

        private sealed class ConditionContext : IBreakpointConditionContext
        {
            private readonly ManagedGameBoyDebugSession session;

            public ConditionContext(ManagedGameBoyDebugSession session, CpuRegisters registers)
            {
                this.session = session;
                Registers = registers;
            }

            public CpuRegisters Registers { get; }

            public DebugResult<byte> ReadByte(ushort address) => DebugResult<byte>.Success(session.ReadByte(address));
        }
    }
}
