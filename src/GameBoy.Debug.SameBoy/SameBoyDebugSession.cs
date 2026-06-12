using System.Globalization;
using System.Text;
using GameBoy.Debug.Core;
using GameBoy.Debug.Symbols;

namespace GameBoy.Debug.SameBoy;

public sealed class SameBoyDebugSession : IGameBoyDebugSession, IDisposable
{
    private const int ScreenWidth = 160;
    private const int ScreenHeight = 144;
    private const int ScreenPixelCount = ScreenWidth * ScreenHeight;
    private static readonly JoypadButton[] CanonicalButtons =
    [
        JoypadButton.Right,
        JoypadButton.Left,
        JoypadButton.Up,
        JoypadButton.Down,
        JoypadButton.A,
        JoypadButton.B,
        JoypadButton.Select,
        JoypadButton.Start,
    ];
    private readonly BreakpointCollection breakpoints = new();
    private readonly SymbolService symbols = new();
    private readonly string artifactDirectory;
    private IntPtr handle;
    private bool disposed;
    private bool romLoaded;
    private string? romTitle;
    private string? romModel;

    public SameBoyDebugSession()
    {
        artifactDirectory = Environment.GetEnvironmentVariable("GAMEBOY_DEBUG_MCP_ARTIFACT_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
    }

    public DebugResult<LoadRomResult> LoadRom(string path)
    {
        var native = EnsureHandle<LoadRomResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        if (!File.Exists(path))
        {
            return DebugResult<LoadRomResult>.Failure("rom_not_found", $"ROM was not found: {path}");
        }

        var title = new StringBuilder(64);
        var model = new StringBuilder(16);
        var result = SameBoyNative.LoadRom(handle, path, title, (UIntPtr)title.Capacity, model, (UIntPtr)model.Capacity);
        if (result != 0)
        {
            return NativeFailure<LoadRomResult>("load_rom_failed");
        }

        breakpoints.ClearAll();
        romLoaded = true;
        romTitle = title.ToString();
        romModel = model.ToString();
        return DebugResult<LoadRomResult>.Success(new LoadRomResult(true, romTitle, romModel));
    }

    public DebugResult<ResetResult> Reset()
    {
        var native = EnsureHandle<ResetResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var result = SameBoyNative.Reset(handle);
        return result == 0
            ? DebugResult<ResetResult>.Success(new ResetResult(true))
            : NativeFailure<ResetResult>("reset_failed");
    }

    public DebugResult<StepInstructionResult> StepInstruction(int count)
    {
        var before = ReadRegisters();
        if (!before.IsSuccess)
        {
            return DebugResult<StepInstructionResult>.Failure(before.Error!.Code, before.Error.Message);
        }

        var disassembly = Disassemble(ParseWord(before.Value.Pc), Math.Min(count, 16));
        for (var i = 0; i < count; i++)
        {
            var step = StepOnce();
            if (!step.IsSuccess)
            {
                return DebugResult<StepInstructionResult>.Failure(step.Error!.Code, step.Error.Message);
            }
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
        var native = EnsureHandle<RunFrameResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        for (var i = 0; i < count; i++)
        {
            if (SameBoyNative.RunFrame(handle) != 0)
            {
                return NativeFailure<RunFrameResult>("run_frame_failed");
            }
        }

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<RunFrameResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        return DebugResult<RunFrameResult>.Success(new RunFrameResult(count, registers.Value, breakpoints.Contains(ParseWord(registers.Value.Pc))));
    }

    public DebugResult<JoypadStateResult> SetJoypad(IReadOnlyList<JoypadButton> pressedButtons)
    {
        var native = EnsureHandle<JoypadStateResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var mask = ToButtonMask(pressedButtons);
        if (!mask.IsSuccess)
        {
            return DebugResult<JoypadStateResult>.Failure(mask.Error!.Code, mask.Error.Message);
        }

        return SameBoyNative.SetJoypad(handle, mask.Value) == 0
            ? DebugResult<JoypadStateResult>.Success(ToJoypadState(mask.Value))
            : NativeFailure<JoypadStateResult>("set_joypad_failed");
    }

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<JoypadButton> pressedButtons, int frameCount)
    {
        var pressed = SetJoypad(pressedButtons);
        if (!pressed.IsSuccess)
        {
            return DebugResult<PressButtonsResult>.Failure(pressed.Error!.Code, pressed.Error.Message);
        }

        var run = RunFrame(frameCount);
        var released = SetJoypad([]);
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
        for (var i = 0; i < maxInstructions; i++)
        {
            var registersBefore = ReadRegisters();
            if (!registersBefore.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(registersBefore.Error!.Code, registersBefore.Error.Message);
            }

            var pcBefore = ParseWord(registersBefore.Value.Pc);
            if (breakpoints.Contains(pcBefore))
            {
                return Stop("breakpoint", registersBefore.Value);
            }

            if (registersBefore.Value.Halted)
            {
                return Stop("halt", registersBefore.Value);
            }

            var step = StepOnce();
            if (!step.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(step.Error!.Code, step.Error.Message);
            }
        }

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        return Stop("maxInstructions", registers.Value);

        static DebugResult<ContinueResult> Stop(string reason, CpuRegisters registers)
        {
            return DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Pc, registers));
        }
    }

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
    {
        var breakpoint = breakpoints.Set(address, condition);
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
        var native = EnsureHandle<CpuRegisters>();
        if (!native.IsSuccess)
        {
            return native;
        }

        return SameBoyNative.ReadRegisters(handle, out var registers) == 0
            ? DebugResult<CpuRegisters>.Success(ToRegisters(registers))
            : NativeFailure<CpuRegisters>("read_registers_failed");
    }

    public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
    {
        var bytes = ReadBytes(address, length);
        return bytes.IsSuccess
            ? DebugResult<MemoryReadResult>.Success(MemoryFormatter.Format(address, bytes.Value))
            : DebugResult<MemoryReadResult>.Failure(bytes.Error!.Code, bytes.Error.Message);
    }

    public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes)
    {
        var native = EnsureHandle<WriteMemoryResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var array = bytes.ToArray();
        return SameBoyNative.WriteMemory(handle, address, array, (UIntPtr)array.Length) == 0
            ? DebugResult<WriteMemoryResult>.Success(new WriteMemoryResult(true, Hex.FormatWord(address), array.Length))
            : NativeFailure<WriteMemoryResult>("write_memory_failed");
    }

    public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount)
    {
        var native = EnsureHandle<DisassembleResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var buffer = new StringBuilder(Math.Max(4096, instructionCount * 96));
        if (SameBoyNative.Disassemble(handle, address, (ushort)instructionCount, buffer, (UIntPtr)buffer.Capacity) != 0)
        {
            return NativeFailure<DisassembleResult>("disassemble_failed");
        }

        var instructions = ParseDisassembly(buffer.ToString(), instructionCount);
        return DebugResult<DisassembleResult>.Success(new DisassembleResult(Hex.FormatWord(address), instructions));
    }

    public DebugResult<OamDumpResult> ReadOam()
    {
        var native = EnsureHandle<OamDumpResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var oam = new byte[0xA0];
        if (SameBoyNative.ReadOam(handle, oam, (UIntPtr)oam.Length) != 0)
        {
            return NativeFailure<OamDumpResult>("read_oam_failed");
        }

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
        var lcdc = ReadIo(0xFF40);
        var stat = ReadIo(0xFF41);
        var scy = ReadIo(0xFF42);
        var scx = ReadIo(0xFF43);
        var ly = ReadIo(0xFF44);
        var lyc = ReadIo(0xFF45);
        var bgp = ReadIo(0xFF47);
        var obp0 = ReadIo(0xFF48);
        var obp1 = ReadIo(0xFF49);
        var wy = ReadIo(0xFF4A);
        var wx = ReadIo(0xFF4B);
        var vbk = ReadIo(0xFF4F);
        var reads = new[] { lcdc, stat, scy, scx, ly, lyc, bgp, obp0, obp1, wy, wx, vbk };
        var failed = reads.FirstOrDefault(result => !result.IsSuccess);
        if (!failed.IsSuccess && failed.Error is not null)
        {
            return DebugResult<PpuStateResult>.Failure(failed.Error.Code, failed.Error.Message);
        }

        return DebugResult<PpuStateResult>.Success(new PpuStateResult(
            Hex.FormatByte(lcdc.Value),
            Hex.FormatByte(stat.Value),
            stat.Value & 0x03,
            Hex.FormatByte(ly.Value),
            Hex.FormatByte(lyc.Value),
            Hex.FormatByte(scy.Value),
            Hex.FormatByte(scx.Value),
            Hex.FormatByte(wy.Value),
            Hex.FormatByte(wx.Value),
            Hex.FormatByte(bgp.Value),
            Hex.FormatByte(obp0.Value),
            Hex.FormatByte(obp1.Value),
            Hex.FormatByte(vbk.Value),
            (lcdc.Value & 0x80) != 0,
            (lcdc.Value & 0x02) != 0,
            (lcdc.Value & 0x20) != 0,
            (lcdc.Value & 0x01) != 0));
    }

    public DebugResult<ScreenCaptureResult> CaptureScreen()
    {
        var native = EnsureHandle<ScreenCaptureResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var pixels = new uint[ScreenPixelCount];
        if (SameBoyNative.CaptureScreen(handle, pixels, (UIntPtr)pixels.Length) != 0)
        {
            return NativeFailure<ScreenCaptureResult>("capture_screen_failed");
        }

        Directory.CreateDirectory(artifactDirectory);
        var path = Path.Combine(artifactDirectory, $"screen-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.bmp");
        WriteBmp(path, pixels);
        return DebugResult<ScreenCaptureResult>.Success(new ScreenCaptureResult(ScreenWidth, ScreenHeight, path));
    }

    public DebugResult<LastWriterResult> FindLastWriter(ushort address)
    {
        var native = EnsureHandle<LastWriterResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var result = SameBoyNative.GetLastWriter(handle, address, out var pc, out var value, out var count);
        if (result < 0)
        {
            return NativeFailure<LastWriterResult>("find_last_writer_failed");
        }

        return DebugResult<LastWriterResult>.Success(result == 0
            ? new LastWriterResult(true, Hex.FormatWord(address), Hex.FormatWord(pc), Hex.FormatByte(value), count)
            : new LastWriterResult(false, Hex.FormatWord(address), null, null, 0));
    }

    public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions)
    {
        var native = EnsureHandle<TraceUntilWriteResult>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var result = SameBoyNative.TraceUntilWrite(handle, address, (uint)maxInstructions, out var instructionsRun, out var pc, out var value);
        if (result < 0)
        {
            return NativeFailure<TraceUntilWriteResult>("trace_until_write_failed");
        }

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<TraceUntilWriteResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        return DebugResult<TraceUntilWriteResult>.Success(result == 0
            ? new TraceUntilWriteResult(true, "write", Hex.FormatWord(address), Hex.FormatWord(pc), Hex.FormatByte(value), instructionsRun, registers.Value)
            : new TraceUntilWriteResult(true, "maxInstructions", Hex.FormatWord(address), null, null, instructionsRun, registers.Value));
    }

    public DebugResult<TilemapDumpResult> DumpTilemap(ushort address)
    {
        var bytes = ReadBytes(address, 32 * 32);
        if (!bytes.IsSuccess)
        {
            return DebugResult<TilemapDumpResult>.Failure(bytes.Error!.Code, bytes.Error.Message);
        }

        var rows = Enumerable.Range(0, 32)
            .Select(row => Hex.FormatBytes(bytes.Value.Skip(row * 32).Take(32)))
            .ToArray();

        return DebugResult<TilemapDumpResult>.Success(new TilemapDumpResult(Hex.FormatWord(address), 32, 32, rows));
    }

    public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount)
    {
        var bytes = ReadBytes(address, tileCount * 16);
        if (!bytes.IsSuccess)
        {
            return DebugResult<TilesetDumpResult>.Failure(bytes.Error!.Code, bytes.Error.Message);
        }

        var tiles = Enumerable.Range(0, tileCount)
            .Select(index => new TileDump(index, Hex.FormatWord((ushort)(address + index * 16)), Hex.FormatBytes(bytes.Value.Skip(index * 16).Take(16))))
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

        var bytes = ReadBytes(resolved.Value.Address, length ?? 1);
        return bytes.IsSuccess
            ? DebugResult<ReadSymbolResult>.Success(new ReadSymbolResult(name, Hex.FormatWord(resolved.Value.Address), bytes.Value, Hex.FormatBytes(bytes.Value)))
            : DebugResult<ReadSymbolResult>.Failure(bytes.Error!.Code, bytes.Error.Message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (handle != IntPtr.Zero)
        {
            SameBoyNative.Destroy(handle);
            handle = IntPtr.Zero;
        }

        romLoaded = false;
        romTitle = null;
        romModel = null;

        disposed = true;
    }

    private DebugResult<byte[]> ReadBytes(ushort address, int length)
    {
        var native = EnsureHandle<byte[]>();
        if (!native.IsSuccess)
        {
            return native;
        }

        var buffer = new byte[length];
        return SameBoyNative.ReadMemory(handle, address, buffer, (UIntPtr)buffer.Length) == 0
            ? DebugResult<byte[]>.Success(buffer)
            : NativeFailure<byte[]>("read_memory_failed");
    }

    private DebugResult<byte> ReadIo(ushort address)
    {
        var bytes = ReadBytes(address, 1);
        return bytes.IsSuccess
            ? DebugResult<byte>.Success(bytes.Value[0])
            : DebugResult<byte>.Failure(bytes.Error!.Code, bytes.Error.Message);
    }

    private DebugResult<bool> StepOnce()
    {
        var native = EnsureHandle<bool>();
        if (!native.IsSuccess)
        {
            return native;
        }

        return SameBoyNative.Step(handle) == 0
            ? DebugResult<bool>.Success(true)
            : NativeFailure<bool>("step_instruction_failed");
    }

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

    private static string ToButtonName(JoypadButton button)
    {
        return button switch
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
    }

    private DebugResult<T> EnsureHandle<T>()
    {
        if (disposed)
        {
            return DebugResult<T>.Failure("session_disposed", "The SameBoy debug session has been disposed.");
        }

        if (handle != IntPtr.Zero)
        {
            return DebugResult<T>.Success(default!);
        }

        try
        {
            handle = SameBoyNative.Create();
            return handle != IntPtr.Zero
                ? DebugResult<T>.Success(default!)
                : DebugResult<T>.Failure("sameboy_create_failed", "SameBoy native session could not be created.");
        }
        catch (DllNotFoundException ex)
        {
            return DebugResult<T>.Failure("sameboy_native_not_found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return DebugResult<T>.Failure("sameboy_bridge_incompatible", ex.Message);
        }
    }

    private DebugResult<T> NativeFailure<T>(string code)
    {
        var buffer = new StringBuilder(512);
        var message = "SameBoy native call failed.";
        if (handle != IntPtr.Zero && SameBoyNative.GetLastError(handle, buffer, (UIntPtr)buffer.Capacity) == 0 && buffer.Length > 0)
        {
            message = buffer.ToString();
        }

        return DebugResult<T>.Failure(code, message);
    }

    private static CpuRegisters ToRegisters(NativeRegisters registers)
    {
        return new CpuRegisters(
            Hex.FormatWord(registers.Af),
            Hex.FormatWord(registers.Bc),
            Hex.FormatWord(registers.De),
            Hex.FormatWord(registers.Hl),
            Hex.FormatWord(registers.Sp),
            Hex.FormatWord(registers.Pc),
            Hex.FormatByte(registers.A),
            Hex.FormatByte(registers.F),
            Hex.FormatByte(registers.B),
            Hex.FormatByte(registers.C),
            Hex.FormatByte(registers.D),
            Hex.FormatByte(registers.E),
            Hex.FormatByte(registers.H),
            Hex.FormatByte(registers.L),
            registers.Ime,
            registers.Halted);
    }

    private IReadOnlyList<DisassembledInstruction> ParseDisassembly(string text, int maxInstructions)
    {
        var parsed = new List<(ushort Address, string Text, string? Symbol)>();
        string? pendingSymbol = null;

        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.EndsWith(':') && !line.Contains(' '))
            {
                pendingSymbol = line[..^1];
                continue;
            }

            if (line.StartsWith("->", StringComparison.Ordinal))
            {
                line = line[2..].TrimStart();
            }

            if (line.Length < 4 || !ushort.TryParse(line[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                continue;
            }

            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
            {
                separator = line.IndexOf(">: ", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    separator++;
                }
            }

            if (separator < 0)
            {
                continue;
            }

            parsed.Add((address, line[(separator + 2)..].Trim(), pendingSymbol));
            pendingSymbol = null;
            if (parsed.Count == maxInstructions)
            {
                break;
            }
        }

        return parsed.Select(item =>
        {
            var bytes = ReadBytes(item.Address, GetInstructionLength(item.Address));
            return new DisassembledInstruction(
                Hex.FormatWord(item.Address),
                bytes.IsSuccess ? Hex.FormatBytes(bytes.Value) : "",
                item.Text,
                item.Symbol);
        }).ToArray();
    }

    private int GetInstructionLength(ushort address)
    {
        var opcode = ReadBytes(address, 1);
        if (!opcode.IsSuccess)
        {
            return 1;
        }

        return InstructionLengths[opcode.Value[0]];
    }

    private static ushort ParseWord(string text)
    {
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return ushort.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static void WriteBmp(string path, IReadOnlyList<uint> pixels)
    {
        const int bytesPerPixel = 3;
        var rowStride = ((ScreenWidth * bytesPerPixel + 3) / 4) * 4;
        var imageSize = rowStride * ScreenHeight;
        var fileSize = 14 + 40 + imageSize;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(ScreenWidth);
        writer.Write(ScreenHeight);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        var padding = new byte[rowStride - ScreenWidth * bytesPerPixel];
        for (var y = ScreenHeight - 1; y >= 0; y--)
        {
            for (var x = 0; x < ScreenWidth; x++)
            {
                var pixel = pixels[y * ScreenWidth + x];
                writer.Write((byte)(pixel & 0xFF));
                writer.Write((byte)((pixel >> 8) & 0xFF));
                writer.Write((byte)((pixel >> 16) & 0xFF));
            }

            writer.Write(padding);
        }
    }

    private static readonly byte[] InstructionLengths =
    [
        1,3,1,1,1,1,2,1,3,1,1,1,1,1,2,1,
        2,3,1,1,1,1,2,1,2,1,1,1,1,1,2,1,
        2,3,1,1,1,1,2,1,2,1,1,1,1,1,2,1,
        2,3,1,1,1,1,2,1,2,1,1,1,1,1,2,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,3,3,3,1,2,1,1,1,3,2,3,3,2,1,
        1,1,3,1,3,1,2,1,1,1,3,1,3,1,2,1,
        2,1,1,1,1,1,2,1,2,1,3,1,1,1,2,1,
        2,1,1,1,1,1,2,1,2,1,3,1,1,1,2,1,
    ];
}
