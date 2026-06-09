using GameBoy.Debug.Core;

namespace GameBoy.Debug.Symbols;

public sealed record SymbolInfo(string Name, ushort Address, int? Bank)
{
    public GameBoyAddress ToAddress() => new(Address, Bank);
}
