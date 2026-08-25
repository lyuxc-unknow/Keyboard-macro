namespace SimToAutoWirte.Models;

public sealed record HotkeyOption(string DisplayName, uint VirtualKey, uint Modifiers = 0)
{
    public override string ToString() => DisplayName;
}
