# GameBoy.Mcp

`GameBoy.Mcp` is a .NET MCP server for inspecting and controlling a Game Boy or Game Boy Color ROM running on the SameBoy core. It is distributed as a .NET tool (command: `gameboymcp`).

The current backend uses a thin native bridge over SameBoy's C core API. It does not implement an emulator.

## Install

The server ships as a .NET tool on NuGet with the Linux x64 native bridge bundled.

Run it on demand with .NET 10's `dnx` (no install required):

```bash
dnx GameBoy.Mcp
```

Or install it globally:

```bash
dotnet tool install -g GameBoy.Mcp
gameboymcp
```

> The published package bundles the `linux-x64` SameBoy native bridge. Other platforms must build the native libraries from source (see [Build](#build)).

## Build

Requirements for the managed project:

- .NET 10 SDK
- `git`, `make`, `cc` or `clang`

Build the managed solution:

```bash
dotnet build gameboy-debug-mcp.slnx
```

Build SameBoy and the native bridge:

```bash
./scripts/build-native.sh
```

That creates:

```text
native/out/linux-x64/libsameboy.so
native/out/linux-x64/libgameboy_debug_sameboy.so
```

You can also point at an existing SameBoy checkout:

```bash
SAMEBOY_DIR=/path/to/SameBoy ./scripts/build-native.sh
```

## Run

From the repo root:

```bash
dotnet run --project src/GameBoy.Debug.Mcp/GameBoy.Debug.Mcp.csproj
```

If the native libraries are somewhere else:

```bash
GAMEBOY_DEBUG_MCP_NATIVE_DIR=/path/to/native dotnet run --project src/GameBoy.Debug.Mcp/GameBoy.Debug.Mcp.csproj
```

## Connect An MCP Client

Use stdio transport. With the tool installed (or via `dnx`), the client entry is path-independent:

```json
{
  "mcpServers": {
    "gameboy": {
      "command": "dnx",
      "args": ["GameBoy.Mcp", "--yes"]
    }
  }
}
```

If you installed the tool globally (`dotnet tool install -g GameBoy.Mcp`), use the command directly:

```json
{
  "mcpServers": {
    "gameboy": {
      "command": "gameboymcp"
    }
  }
}
```

For development against a local checkout you can still run it from source:

```json
{
  "mcpServers": {
    "gameboy": {
      "command": "dotnet",
      "args": ["run", "--project", "src/GameBoy.Debug.Mcp/GameBoy.Debug.Mcp.csproj"],
      "env": {
        "GAMEBOY_DEBUG_MCP_NATIVE_DIR": "native/out/linux-x64"
      }
    }
  }
}
```

## Tools

Implemented tools:

- `load_rom`
- `save_state`
- `load_state`
- `reset`
- `step_instruction`
- `run_frame`
- `set_joypad`
- `press_buttons`
- `continue_until_break`
- `set_breakpoint`
- `clear_breakpoint`
- `list_breakpoints`
- `get_state`
- `read_registers`
- `read_memory`
- `write_memory`
- `disassemble`
- `load_symbols`
- `resolve_symbol`
- `read_symbol`
- `dump_oam`
- `read_ppu_state`
- `capture_screen`
- `find_last_writer`
- `trace_until_write`
- `dump_tilemap`
- `dump_tileset`

See [docs/mcp-tools.md](docs/mcp-tools.md) for schemas and examples.

## Current Limitations

- The native bridge has been validated on Linux x64.
- The backend skips the external boot ROM and applies a standard post-boot register state. This is deterministic and practical for debugging, but it is not a boot-ROM-accurate startup trace.
- Breakpoints are managed by the C# session loop. Conditional breakpoints support register and memory comparisons such as `A == 0x10`, `HL >= 0xC000`, and `[HL] < 4`; SameBoy's native conditional breakpoint engine is not exposed.
- `.sym` parsing is intentionally simple: `BANK:ADDR Name` and `ADDR Name` lines with `;` or `#` comments.
- `capture_screen` returns inline PNG image content.
- Joypad control disables SameBoy's physical button-bounce emulation so MCP-driven tests are deterministic.

## SameBoy Approach

SameBoy exposes the core operations this server needs through its C API: ROM loading, savestates, reset, stepping via `GB_run`, frame execution, joypad input, registers, memory, direct OAM access, disassembly logging, and framebuffer output. The bridge in `native/sameboy_mcp_bridge.c` converts those APIs into stable P/Invoke-friendly functions.

See [docs/sameboy-integration.md](docs/sameboy-integration.md) for the research note.

## Test

```bash
dotnet test gameboy-debug-mcp.slnx
```

If `native/out/linux-x64/libgameboy_debug_sameboy.so` exists, the test suite also runs a SameBoy integration test against a generated minimal ROM.

## Deployment

Distribution is handled by [DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer), configured in [`deployer.yaml`](deployer.yaml). It packs the `GameBoy.Mcp` tool (the native Linux x64 bridge is built on demand and bundled into the package) and pushes it to NuGet.

Publish locally (requires the `NUGET_API_KEY` environment variable):

```bash
dnx dotnetdeployer.tool            # add --dry-run to pack without publishing
```

CI/CD is provided by [DotnetDeployer.Fleet](https://github.com/SuperJMN/DotnetDeployer.Fleet): a coordinator/worker that clones the repo and runs DotnetDeployer on every commit. The worker that builds the package must be `linux-x64` with `git`, `make` and `cc`/`clang` available so the native bridge can be compiled.

