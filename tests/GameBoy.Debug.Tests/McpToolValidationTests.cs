using GameBoy.Debug.Core;
using GameBoy.Debug.Mcp;
using ModelContextProtocol.Protocol;

namespace GameBoy.Debug.Tests;

public sealed class McpToolValidationTests
{
    [Fact]
    public void Read_memory_rejects_non_positive_length_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.ReadMemory(session, "0xC000", 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_length", error.Error.Code);
        Assert.False(session.ReadMemoryCalled);
    }

    [Fact]
    public void Read_memory_returns_session_payload_for_valid_input()
    {
        var session = new FakeDebugSession
        {
            ReadMemoryResult = DebugResult<MemoryReadResult>.Success(
                new MemoryReadResult("0xC000", "2A", [0x2A], "*")),
        };

        var result = GameBoyDebugTools.ReadMemory(session, "0xC000", 1);

        var payload = Assert.IsType<MemoryReadResult>(result);
        Assert.True(session.ReadMemoryCalled);
        Assert.Equal((ushort)0xC000, session.LastReadAddress);
        Assert.Equal(1, session.LastReadLength);
        Assert.Equal("2A", payload.BytesHex);
    }

    [Fact]
    public void Set_joypad_rejects_unknown_buttons_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.SetJoypad(session, ["right", "jump"]);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_button", error.Error.Code);
        Assert.False(session.SetJoypadCalled);
    }

    [Fact]
    public void Set_joypad_sends_normalized_button_set_to_session()
    {
        var session = new FakeDebugSession
        {
            SetJoypadResult = DebugResult<JoypadStateResult>.Success(
                new JoypadStateResult(true, false, false, false, true, false, false, false, ["right", "a"])),
        };

        var result = GameBoyDebugTools.SetJoypad(session, ["RIGHT", "a", "right"]);

        var payload = Assert.IsType<JoypadStateResult>(result);
        Assert.True(session.SetJoypadCalled);
        Assert.Equal([JoypadButton.Right, JoypadButton.A], session.LastJoypadButtons);
        Assert.Equal(["right", "a"], payload.Pressed);
    }

    [Fact]
    public void Press_buttons_validates_frame_count_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.PressButtons(session, ["a"], 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_frame_count", error.Error.Code);
        Assert.False(session.PressButtonsCalled);
    }

    [Fact]
    public void Press_buttons_sends_normalized_button_set_and_frame_count_to_session()
    {
        var registers = new CpuRegisters(
            "0x0000", "0x0000", "0x0000", "0x0000", "0xFFFE", "0x0150",
            "0x00", "0x00", "0x00", "0x00", "0x00", "0x00", "0x00", "0x00",
            false, false);
        var session = new FakeDebugSession
        {
            PressButtonsResult = DebugResult<PressButtonsResult>.Success(
                new PressButtonsResult(5, new JoypadStateResult(false, false, false, false, false, false, false, false, []), registers)),
        };

        var result = GameBoyDebugTools.PressButtons(session, ["left", "b"], 5);

        var payload = Assert.IsType<PressButtonsResult>(result);
        Assert.True(session.PressButtonsCalled);
        Assert.Equal([JoypadButton.Left, JoypadButton.B], session.LastPressedButtons);
        Assert.Equal(5, session.LastPressFrameCount);
        Assert.Equal(5, payload.FramesRun);
    }

    [Fact]
    public void List_breakpoints_returns_session_payload()
    {
        var session = new FakeDebugSession
        {
            ListBreakpointsResult = DebugResult<ListBreakpointsResult>.Success(
                new ListBreakpointsResult(
                [
                    new BreakpointEntry("bp-1", "0x0150", true, null),
                    new BreakpointEntry("bp-2", "0xC000", true, "a == 1"),
                ])),
        };

        var result = GameBoyDebugTools.ListBreakpoints(session);

        var payload = Assert.IsType<ListBreakpointsResult>(result);
        Assert.True(session.ListBreakpointsCalled);
        Assert.Equal(2, payload.Breakpoints.Count);
        Assert.Equal("bp-2", payload.Breakpoints[1].Id);
        Assert.Equal("a == 1", payload.Breakpoints[1].Condition);
    }

    [Fact]
    public void Get_state_returns_session_payload()
    {
        var session = new FakeDebugSession
        {
            GetStateResult = DebugResult<SessionStateResult>.Success(
                new SessionStateResult(true, "MCPTEST", "DMG", false, "0x0100")),
        };

        var result = GameBoyDebugTools.GetState(session);

        var payload = Assert.IsType<SessionStateResult>(result);
        Assert.True(session.GetStateCalled);
        Assert.True(payload.RomLoaded);
        Assert.Equal("MCPTEST", payload.Title);
        Assert.Equal("DMG", payload.Model);
        Assert.Equal("0x0100", payload.Pc);
    }

    [Fact]
    public void Capture_screen_returns_inline_png_image_content()
    {
        var pngBytes = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
        var session = new FakeDebugSession
        {
            CaptureScreenResult = DebugResult<ScreenCaptureResult>.Success(
                new ScreenCaptureResult(160, 144, "image/png", pngBytes)),
        };

        var result = GameBoyDebugTools.CaptureScreen(session);

        var image = Assert.IsType<ImageContentBlock>(result);
        Assert.True(session.CaptureScreenCalled);
        Assert.Equal("image", image.Type);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Convert.ToBase64String(pngBytes), System.Text.Encoding.UTF8.GetString(image.Data.Span));
        Assert.Equal(pngBytes, image.DecodedData.ToArray());
    }

    [Fact]
    public void Set_breakpoint_rejects_invalid_condition_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.SetBreakpoint(session, "0x0150", "A = 1");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_breakpoint_condition", error.Error.Code);
        Assert.False(session.SetBreakpointCalled);
    }

    [Fact]
    public void Save_state_rejects_blank_path_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.SaveState(session, " ");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_path", error.Error.Code);
        Assert.False(session.SaveStateCalled);
    }

    [Fact]
    public void Save_state_returns_session_payload_for_valid_path()
    {
        var session = new FakeDebugSession
        {
            SaveStateResult = DebugResult<SaveStateResult>.Success(new SaveStateResult(true, "state.s0")),
        };

        var result = GameBoyDebugTools.SaveState(session, "state.s0");

        var payload = Assert.IsType<SaveStateResult>(result);
        Assert.True(session.SaveStateCalled);
        Assert.Equal("state.s0", session.LastSaveStatePath);
        Assert.Equal("state.s0", payload.Path);
    }

    [Fact]
    public void Load_state_rejects_blank_path_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = GameBoyDebugTools.LoadState(session, "");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_path", error.Error.Code);
        Assert.False(session.LoadStateCalled);
    }

    [Fact]
    public void Load_state_returns_session_payload_for_valid_path()
    {
        var session = new FakeDebugSession
        {
            LoadStateResult = DebugResult<LoadStateResult>.Success(new LoadStateResult(true, "state.s0")),
        };

        var result = GameBoyDebugTools.LoadState(session, "state.s0");

        var payload = Assert.IsType<LoadStateResult>(result);
        Assert.True(session.LoadStateCalled);
        Assert.Equal("state.s0", session.LastLoadStatePath);
        Assert.Equal("state.s0", payload.Path);
    }

    private sealed class FakeDebugSession : IGameBoyDebugSession
    {
        public bool ReadMemoryCalled { get; private set; }

        public ushort LastReadAddress { get; private set; }

        public int LastReadLength { get; private set; }

        public bool SetJoypadCalled { get; private set; }

        public IReadOnlyList<JoypadButton> LastJoypadButtons { get; private set; } = [];

        public bool PressButtonsCalled { get; private set; }

        public IReadOnlyList<JoypadButton> LastPressedButtons { get; private set; } = [];

        public int LastPressFrameCount { get; private set; }

        public bool ListBreakpointsCalled { get; private set; }

        public bool GetStateCalled { get; private set; }

        public bool CaptureScreenCalled { get; private set; }

        public bool SetBreakpointCalled { get; private set; }

        public bool SaveStateCalled { get; private set; }

        public string? LastSaveStatePath { get; private set; }

        public bool LoadStateCalled { get; private set; }

        public string? LastLoadStatePath { get; private set; }

        public DebugResult<MemoryReadResult> ReadMemoryResult { get; init; } =
            DebugResult<MemoryReadResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<JoypadStateResult> SetJoypadResult { get; init; } =
            DebugResult<JoypadStateResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<PressButtonsResult> PressButtonsResult { get; init; } =
            DebugResult<PressButtonsResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<ListBreakpointsResult> ListBreakpointsResult { get; init; } =
            DebugResult<ListBreakpointsResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<SessionStateResult> GetStateResult { get; init; } =
            DebugResult<SessionStateResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<ScreenCaptureResult> CaptureScreenResult { get; init; } =
            DebugResult<ScreenCaptureResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<SaveStateResult> SaveStateResult { get; init; } =
            DebugResult<SaveStateResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<LoadStateResult> LoadStateResult { get; init; } =
            DebugResult<LoadStateResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<LoadRomResult> LoadRom(string path) => throw new NotSupportedException();

        public DebugResult<SaveStateResult> SaveState(string path)
        {
            SaveStateCalled = true;
            LastSaveStatePath = path;
            return SaveStateResult;
        }

        public DebugResult<LoadStateResult> LoadState(string path)
        {
            LoadStateCalled = true;
            LastLoadStatePath = path;
            return LoadStateResult;
        }

        public DebugResult<ResetResult> Reset() => throw new NotSupportedException();

        public DebugResult<StepInstructionResult> StepInstruction(int count) => throw new NotSupportedException();

        public DebugResult<RunFrameResult> RunFrame(int count) => throw new NotSupportedException();

        public DebugResult<JoypadStateResult> SetJoypad(IReadOnlyList<JoypadButton> pressedButtons)
        {
            SetJoypadCalled = true;
            LastJoypadButtons = pressedButtons.ToArray();
            return SetJoypadResult;
        }

        public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<JoypadButton> pressedButtons, int frameCount)
        {
            PressButtonsCalled = true;
            LastPressedButtons = pressedButtons.ToArray();
            LastPressFrameCount = frameCount;
            return PressButtonsResult;
        }

        public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions) => throw new NotSupportedException();

        public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
        {
            SetBreakpointCalled = true;
            return DebugResult<BreakpointSetResult>.Failure("not_configured", "The fake session was not configured.");
        }

        public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) => throw new NotSupportedException();

        public DebugResult<ListBreakpointsResult> ListBreakpoints()
        {
            ListBreakpointsCalled = true;
            return ListBreakpointsResult;
        }

        public DebugResult<SessionStateResult> GetState()
        {
            GetStateCalled = true;
            return GetStateResult;
        }

        public DebugResult<CpuRegisters> ReadRegisters() => throw new NotSupportedException();

        public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
        {
            ReadMemoryCalled = true;
            LastReadAddress = address;
            LastReadLength = length;
            return ReadMemoryResult;
        }

        public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes) => throw new NotSupportedException();

        public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount) => throw new NotSupportedException();

        public DebugResult<OamDumpResult> ReadOam() => throw new NotSupportedException();

        public DebugResult<PpuStateResult> ReadPpuState() => throw new NotSupportedException();

        public DebugResult<ScreenCaptureResult> CaptureScreen()
        {
            CaptureScreenCalled = true;
            return CaptureScreenResult;
        }

        public DebugResult<LastWriterResult> FindLastWriter(ushort address) => throw new NotSupportedException();

        public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions) => throw new NotSupportedException();

        public DebugResult<TilemapDumpResult> DumpTilemap(ushort address) => throw new NotSupportedException();

        public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount) => throw new NotSupportedException();

        public DebugResult<LoadSymbolsResult> LoadSymbols(string path) => throw new NotSupportedException();

        public DebugResult<ResolveSymbolResult> ResolveSymbol(string name) => throw new NotSupportedException();

        public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length) => throw new NotSupportedException();
    }
}
