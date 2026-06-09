using GameBoy.Debug.Core;
using GameBoy.Debug.Mcp;

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

    private sealed class FakeDebugSession : IGameBoyDebugSession
    {
        public bool ReadMemoryCalled { get; private set; }

        public ushort LastReadAddress { get; private set; }

        public int LastReadLength { get; private set; }

        public DebugResult<MemoryReadResult> ReadMemoryResult { get; init; } =
            DebugResult<MemoryReadResult>.Failure("not_configured", "The fake session was not configured.");

        public DebugResult<LoadRomResult> LoadRom(string path) => throw new NotSupportedException();

        public DebugResult<ResetResult> Reset() => throw new NotSupportedException();

        public DebugResult<StepInstructionResult> StepInstruction(int count) => throw new NotSupportedException();

        public DebugResult<RunFrameResult> RunFrame(int count) => throw new NotSupportedException();

        public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions) => throw new NotSupportedException();

        public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition) => throw new NotSupportedException();

        public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) => throw new NotSupportedException();

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

        public DebugResult<ScreenCaptureResult> CaptureScreen() => throw new NotSupportedException();

        public DebugResult<LastWriterResult> FindLastWriter(ushort address) => throw new NotSupportedException();

        public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions) => throw new NotSupportedException();

        public DebugResult<TilemapDumpResult> DumpTilemap(ushort address) => throw new NotSupportedException();

        public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount) => throw new NotSupportedException();

        public DebugResult<LoadSymbolsResult> LoadSymbols(string path) => throw new NotSupportedException();

        public DebugResult<ResolveSymbolResult> ResolveSymbol(string name) => throw new NotSupportedException();

        public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length) => throw new NotSupportedException();
    }
}
