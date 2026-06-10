namespace GameBoy.Debug.Core;

public interface IGameBoyDebugSession
{
    DebugResult<LoadRomResult> LoadRom(string path);

    DebugResult<ResetResult> Reset();

    DebugResult<StepInstructionResult> StepInstruction(int count);

    DebugResult<RunFrameResult> RunFrame(int count);

    DebugResult<JoypadStateResult> SetJoypad(IReadOnlyList<JoypadButton> pressedButtons);

    DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<JoypadButton> pressedButtons, int frameCount);

    DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions);

    DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition);

    DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId);

    DebugResult<CpuRegisters> ReadRegisters();

    DebugResult<MemoryReadResult> ReadMemory(ushort address, int length);

    DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes);

    DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount);

    DebugResult<OamDumpResult> ReadOam();

    DebugResult<PpuStateResult> ReadPpuState();

    DebugResult<ScreenCaptureResult> CaptureScreen();

    DebugResult<LastWriterResult> FindLastWriter(ushort address);

    DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions);

    DebugResult<TilemapDumpResult> DumpTilemap(ushort address);

    DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount);

    DebugResult<LoadSymbolsResult> LoadSymbols(string path);

    DebugResult<ResolveSymbolResult> ResolveSymbol(string name);

    DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length);
}
