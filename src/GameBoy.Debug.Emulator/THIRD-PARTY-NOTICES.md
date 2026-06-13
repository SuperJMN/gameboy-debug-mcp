# Third-Party Notices

The `CoreBoy/` subfolder contains vendored third-party emulator core code from CoreBoy (https://github.com/davidwhitney/CoreBoy), which is based on coffee-gb (https://github.com/trekawek/coffee-gb) by Tomasz Rękawek.

CoreBoy and coffee-gb are licensed under the MIT License. The vendored license text is preserved at `CoreBoy/LICENSE`.

## Modifications

The vendored `CoreBoy/` sources were trimmed and lightly adapted for a headless debugging
host:

- Removed UI/audio frontends and their dependencies (ImageSharp, NAudio, Newtonsoft.Json,
  CommandLineParser): `gui/BitmapDisplay`, `gui/GameboyDisplayFrame`, `gui/WinSound`,
  `gui/Emulator`, `gui/ConsoleWriteSerialEndpoint`, the `debugging/` command system, and
  `memory/cart/battery/FileBattery`.
- `GameboyOptions`: removed `CommandLine` attributes.
- `memory/cart/Cartridge`: always uses `NullBattery` (battery saves are not needed).
- `cpu/Cpu`: added a read-only `InterruptMasterEnabled` accessor (IME is not memory-mapped).
- `memory/Mmu`: added a `WriteObserver` hook to support the find_last_writer / trace_until_write
  debugging tools.

All other emulation logic is unchanged from CoreBoy.
