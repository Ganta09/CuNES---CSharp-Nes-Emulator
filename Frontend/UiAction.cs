namespace cunes.Frontend;

public enum UiActionType
{
    LoadRom,
    CloseRom,
    Exit
}

public readonly record struct UiAction(UiActionType Type, string? RomPath = null);
