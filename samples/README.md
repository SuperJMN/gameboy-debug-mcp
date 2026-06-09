# Sample Interaction

This example assumes a ROM at `/tmp/demo.gb` and optional symbols at `/tmp/demo.sym`.

1. Load the ROM:

```json
{ "tool": "load_rom", "arguments": { "path": "/tmp/demo.gb" } }
```

2. Read the initial CPU state:

```json
{ "tool": "read_registers", "arguments": {} }
```

3. Inspect the entry point:

```json
{ "tool": "disassemble", "arguments": { "address": "0x0100", "instructionCount": 8 } }
```

4. Step one instruction:

```json
{ "tool": "step_instruction", "arguments": { "count": 1 } }
```

5. Read work RAM:

```json
{ "tool": "read_memory", "arguments": { "address": "0xC000", "length": 16 } }
```

6. Set a breakpoint and continue:

```json
{ "tool": "set_breakpoint", "arguments": { "address": "0x0150", "condition": null } }
```

```json
{ "tool": "continue_until_break", "arguments": { "maxInstructions": 1000000 } }
```

7. Load symbols and read a variable:

```json
{ "tool": "load_symbols", "arguments": { "path": "/tmp/demo.sym" } }
```

```json
{ "tool": "read_symbol", "arguments": { "name": "Player.X", "length": 1 } }
```

8. Inspect sprites and capture the screen:

```json
{ "tool": "dump_oam", "arguments": {} }
```

```json
{ "tool": "read_ppu_state", "arguments": {} }
```

```json
{ "tool": "capture_screen", "arguments": {} }
```

9. Trace a write and inspect tile data:

```json
{ "tool": "trace_until_write", "arguments": { "address": "0xC000", "maxInstructions": 1000000 } }
```

```json
{ "tool": "find_last_writer", "arguments": { "address": "0xC000" } }
```

```json
{ "tool": "dump_tilemap", "arguments": { "address": "0x9800" } }
```

```json
{ "tool": "dump_tileset", "arguments": { "address": "0x8000", "tileCount": 16 } }
```
