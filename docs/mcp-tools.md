# MCP Tools

All addresses are hexadecimal strings. Inputs are bounded; trace-like tools require explicit limits.

## load_rom

Input:

```json
{ "path": "path/to/game.gb" }
```

Output:

```json
{ "loaded": true, "romTitle": "GAME", "model": "DMG" }
```

## reset

Input:

```json
{}
```

Output:

```json
{ "reset": true }
```

## step_instruction

Input:

```json
{ "count": 1 }
```

Output:

```json
{
  "pcBefore": "0x0100",
  "pcAfter": "0x0101",
  "registers": {},
  "disassembly": "0x0100: NOP"
}
```

## run_frame

Input:

```json
{ "count": 1 }
```

Output:

```json
{ "framesRun": 1, "registers": {}, "hitBreakpoint": false }
```

## set_joypad

Input:

```json
{ "buttons": ["right", "a"] }
```

Output:

```json
{
  "right": true,
  "left": false,
  "up": false,
  "down": false,
  "a": true,
  "b": false,
  "select": false,
  "start": false,
  "pressed": ["right", "a"]
}
```

Valid button names are `right`, `left`, `up`, `down`, `a`, `b`, `select`, and `start`. Pass an empty array to release every button. SameBoy's physical button-bounce emulation is disabled by the bridge so MCP-driven input is deterministic.

## press_buttons

Input:

```json
{ "buttons": ["a"], "frameCount": 6 }
```

Output:

```json
{ "framesRun": 6, "released": { "pressed": [] }, "registers": {} }
```

This holds the requested buttons for `frameCount` frames, then releases every button before returning. `frameCount` must be between 1 and 600.

## continue_until_break

Input:

```json
{ "maxInstructions": 1000000 }
```

Output:

```json
{ "stopped": true, "reason": "breakpoint", "pc": "0x0150", "registers": {} }
```

Reasons: `breakpoint`, `maxInstructions`, `halt`, `error`.

## set_breakpoint

Input:

```json
{ "address": "0x1234", "condition": null }
```

Output:

```json
{ "breakpointId": "bp-1", "address": "0x1234", "enabled": true }
```

Conditions are accepted for API compatibility but are not evaluated by the first SameBoy backend.

## clear_breakpoint

Input:

```json
{ "breakpointId": "bp-1" }
```

Output:

```json
{ "cleared": true }
```

## list_breakpoints

Input:

```json
{}
```

Output:

```json
{
  "breakpoints": [
    { "id": "bp-1", "address": "0x0150", "enabled": true, "condition": null },
    { "id": "bp-2", "address": "0xC000", "enabled": true, "condition": "a == 1" }
  ]
}
```

## get_state

Input:

```json
{}
```

Output with a ROM loaded:

```json
{ "romLoaded": true, "title": "GAME", "model": "DMG", "halted": false, "pc": "0x0100" }
```

Output before loading a ROM:

```json
{ "romLoaded": false, "title": null, "model": null, "halted": false, "pc": null }
```

## read_registers

Input:

```json
{}
```

Output:

```json
{
  "af": "0x01B0",
  "bc": "0x0013",
  "de": "0x00D8",
  "hl": "0x014D",
  "sp": "0xFFFE",
  "pc": "0x0100",
  "a": "0x01",
  "f": "0xB0",
  "b": "0x00",
  "c": "0x13",
  "d": "0x00",
  "e": "0xD8",
  "h": "0x01",
  "l": "0x4D",
  "ime": false,
  "halted": false
}
```

## read_memory

Input:

```json
{ "address": "0xC000", "length": 32 }
```

Output:

```json
{ "address": "0xC000", "bytesHex": "00 01 02", "bytes": [0, 1, 2], "ascii": "..." }
```

## write_memory

Input:

```json
{ "address": "0xC000", "bytes": [1, 2, 3] }
```

Output:

```json
{ "written": true, "address": "0xC000", "length": 3 }
```

## disassemble

Input:

```json
{ "address": "0x0150", "instructionCount": 16 }
```

Output:

```json
{
  "address": "0x0150",
  "instructions": [
    { "address": "0x0150", "bytes": "3E 01", "text": "LD A, $01", "symbol": null }
  ]
}
```

## load_symbols

Input:

```json
{ "path": "path/to/game.sym" }
```

Output:

```json
{ "loaded": true, "symbolCount": 1234 }
```

## resolve_symbol

Input:

```json
{ "name": "Player.X" }
```

Output:

```json
{ "name": "Player.X", "address": "0xC120", "bank": null }
```

## read_symbol

Input:

```json
{ "name": "Player.X", "length": 1 }
```

Output:

```json
{ "name": "Player.X", "address": "0xC120", "bytes": [42], "bytesHex": "2A" }
```

## dump_oam

Input:

```json
{}
```

Output:

```json
{
  "sprites": [
    { "index": 0, "y": 16, "x": 32, "tile": "0x04", "attributes": "0x00", "visible": true }
  ]
}
```

## read_ppu_state

Input:

```json
{}
```

Output:

```json
{
  "lcdc": "0x91",
  "stat": "0x85",
  "mode": 1,
  "ly": "0x90",
  "lyc": "0x00",
  "scy": "0x00",
  "scx": "0x00",
  "wy": "0x00",
  "wx": "0x00",
  "bgp": "0xFC",
  "obp0": "0xFF",
  "obp1": "0xFF",
  "vbk": "0x00",
  "lcdEnabled": true,
  "spritesEnabled": true,
  "windowEnabled": false,
  "backgroundEnabled": true
}
```

## capture_screen

Input:

```json
{}
```

Output:

```json
{ "width": 160, "height": 144, "imagePath": "artifacts/screen-20260610-000000-000.bmp" }
```

## find_last_writer

Input:

```json
{ "address": "0xC000" }
```

Output:

```json
{ "found": true, "address": "0xC000", "pc": "0x0105", "value": "0x2A", "writeCount": 1 }
```

This reports writes observed after the session started. It is not a time-travel query for writes that happened before the backend was running.

## trace_until_write

Input:

```json
{ "address": "0xC000", "maxInstructions": 1000000 }
```

Output:

```json
{
  "stopped": true,
  "reason": "write",
  "address": "0xC000",
  "pc": "0x0105",
  "value": "0x2A",
  "instructionsRun": 12,
  "registers": {}
}
```

Reasons: `write`, `maxInstructions`.

## dump_tilemap

Input:

```json
{ "address": "0x9800" }
```

Output:

```json
{ "address": "0x9800", "width": 32, "height": 32, "rows": ["00 01 ..."] }
```

The address must be `0x9800` or `0x9C00`.

## dump_tileset

Input:

```json
{ "address": "0x8000", "tileCount": 16 }
```

Output:

```json
{
  "address": "0x8000",
  "tileCount": 16,
  "tiles": [
    { "index": 0, "address": "0x8000", "bytesHex": "00 00 ..." }
  ]
}
```

## Errors

Expected failures are returned as structured objects:

```json
{
  "error": {
    "code": "invalid_address",
    "message": "'zzzz' is not a valid Game Boy address."
  }
}
```
