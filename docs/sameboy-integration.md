# SameBoy Integration Note

Research date: 2026-06-10.

Sources inspected:

- SameBoy repository cloned from `https://github.com/LIJI32/SameBoy.git`
- `Core/gb.h`
- `Core/gb.c`
- `Core/memory.h`
- `Core/debugger.h`
- `Core/sm83_cpu.h`
- `Core/sm83_disassembler.c`
- SameBoy `Makefile`
- MCP C# SDK docs: <https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html>

## 1. Can We Link Directly Against SameBoy's Core API?

Yes. SameBoy has a `make lib` target that builds `build/lib/libsameboy.so` on Linux. The public core API includes `GB_alloc`, `GB_init`, `GB_free`, `GB_dealloc`, `GB_load_rom`, `GB_reset`, `GB_run`, `GB_run_frame`, `GB_set_key_mask`, `GB_get_registers`, `GB_get_direct_access`, `GB_get_pixels_output`, and callback setup functions.

The chosen approach links a small C bridge against `libsameboy.so` and calls that bridge from .NET via P/Invoke.

## 2. Can We Programmatically Control Stepping, Memory, Registers, And Breakpoints?

Stepping, memory, and registers are directly available:

- `GB_run` executes the next CPU run slice, which is suitable for instruction stepping in this prototype.
- `GB_safe_read_memory` reads without normal read side effects.
- `GB_write_memory` writes CPU address-space memory.
- `GB_get_registers` and internal state fields expose CPU registers.

SameBoy's textual debugger has breakpoint support, but the public C header does not expose a stable breakpoint CRUD API. For the first version, C# owns a simple breakpoint collection and `continue_until_break` compares the current `PC` in a bounded step loop.

Write tracing uses `GB_set_write_memory_callback`. The native bridge records the last observed writer for each 16-bit address and supports bounded `trace_until_write` execution.

Joypad input uses `GB_set_key_mask` with the SameBoy button order from `Core/joypad.h`: right, left, up, down, A, B, select, start. The bridge disables `GB_set_emulate_joypad_bouncing` because MCP-driven tests should be deterministic instead of reproducing physical switch bounce noise.

## 3. Is The Textual Debugger Easier To Automate Initially?

No. SameBoy's SDL frontend supports a textual debugger, but automating it would make startup, prompts, terminal I/O, and parsing less deterministic. The C core API is a better first integration path because it provides direct calls for the required state and execution operations.

The bridge still reuses SameBoy's disassembler by capturing `GB_log` output from `GB_cpu_disassemble`.

## 4. How Does SameBoy Expose Or Store CPU Registers?

`Core/gb.h` defines `GB_registers_t` and stores registers in the core state as `af`, `bc`, `de`, `hl`, `sp`, `pc` plus byte views such as `a`, `f`, `b`, `c`, `d`, `e`, `h`, and `l`. The bridge compiles with `GB_INTERNAL`, matching SameBoy's own core objects, so it can read these fields and return a flat native struct to .NET.

## 5. How Can We Read Memory Safely?

SameBoy exposes `GB_safe_read_memory(GB_gameboy_t *, uint16_t)`, documented in `Core/memory.h` as reading without side effects. The MCP `read_memory` and disassembly-byte formatting paths use this safe read.

Writes use `GB_write_memory`.

## 6. How Can We Capture The Framebuffer?

The core does not allocate a framebuffer by default. The bridge owns a 160x144 `uint32_t` buffer, registers it with `GB_set_pixels_output`, and provides a simple RGB encoder via `GB_set_rgb_encode_callback`.

The C# session copies that buffer and encodes it as an inline 8-bit RGB PNG for the MCP `capture_screen` tool.

## 7. What Build System Changes Are Needed?

The repo adds `scripts/build-native.sh`. It:

1. Clones SameBoy into `external/SameBoy` unless `SAMEBOY_DIR` points to an existing checkout.
2. Runs `make lib CONF=release`.
3. Compiles `native/sameboy_mcp_bridge.c` with `-DGB_INTERNAL`.
4. Copies `libsameboy.so` and `libgameboy_debug_sameboy.so` to `native/out/linux-x64`.

The managed `.NET` solution builds independently, but runtime SameBoy tools require the native output or `GAMEBOY_DEBUG_MCP_NATIVE_DIR`.

## 8. What Platforms Are Supported By The Chosen Approach?

Validated now:

- Linux x64

The architecture is portable, but the build script currently emits Linux `.so` files. macOS and Windows need equivalent compiler/linker commands and library suffix handling.

## 9. What Are The Risks?

- The bridge uses `GB_INTERNAL` to access complete core state. This is practical but can break if SameBoy changes internal struct layout.
- The first version skips an external boot ROM and applies a deterministic post-boot register state.
- Breakpoints are simpler than SameBoy's textual debugger breakpoints.
- Last-writer data only covers writes observed while this backend is active.
- Disassembly is captured from SameBoy's logger and parsed in C#.
- SameBoy's `make lib` may print warnings about optional frontend dependencies even when the core library is produced.
