using DoinkBinds.Models;
using DoinkBinds.Services;

namespace DoinkBinds.ViewModels;

/// <summary>Library operations that individual macro/folder items can request from the main view model.</summary>
public interface ILibraryActions
{
    Task RenameMacroAsync(MacroViewModel macro);
    Task DuplicateMacroAsync(MacroViewModel macro);
    Task DeleteMacroAsync(MacroViewModel macro);
    Task MoveMacroAsync(MacroViewModel macro);
    Task ExportMacroAsync(MacroViewModel macro);
    void PlayMacro(MacroViewModel macro);
    Task RenameFolderAsync(FolderViewModel folder);
    Task DeleteFolderAsync(FolderViewModel folder);
    void NewMacroInFolder(FolderViewModel folder);
    void MacroChanged(MacroViewModel macro);
    void RefreshTriggers();
    void Toast(string message, ToastKind kind = ToastKind.Info);
}

/// <summary>Everything a <see cref="MacroViewModel"/> needs, bundled to keep constructors short.</summary>
public sealed class EditorContext(
    IMousePositionService positions,
    IHotkeyService hotkeys,
    ISettingsService settings,
    IDialogService dialogs,
    ILibraryActions library)
{
    public IMousePositionService Positions { get; } = positions;
    public IHotkeyService Hotkeys { get; } = hotkeys;
    public ISettingsService Settings { get; } = settings;
    public IDialogService Dialogs { get; } = dialogs;
    public ILibraryActions Library { get; } = library;
}

/// <summary>Static option lists for combo boxes.</summary>
public static class Options
{
    public static IReadOnlyList<MousePositionMode> MouseModes { get; } = Enum.GetValues<MousePositionMode>();
    public static IReadOnlyList<MonitorAwareness> MonitorAwarenessModes { get; } = Enum.GetValues<MonitorAwareness>();
    public static IReadOnlyList<MacroMouseButton> MouseButtons { get; } = Enum.GetValues<MacroMouseButton>();
    public static IReadOnlyList<HotkeyScope> Scopes { get; } = Enum.GetValues<HotkeyScope>();
    public static IReadOnlyList<MacroActionType> ActionTypes { get; } = Enum.GetValues<MacroActionType>();
    public static IReadOnlyList<string> Themes { get; } = new[] { "Dark", "Light", "System" };
    public static IReadOnlyList<KeyOption> Keys { get; } =
        KeyCodes.All.Select(k => new KeyOption(k.Key, k.Value)).OrderBy(k => k.SortKey).ToList();
}

public sealed record KeyOption(int Code, string Name)
{
    // Letters, digits, F-keys first; then everything else alphabetically.
    public string SortKey => Name.Length == 1 ? "0" + Name : Name.StartsWith('F') && Name.Length <= 3 ? "1" + Name.PadLeft(3) : "2" + Name;
    public override string ToString() => Name;
}
