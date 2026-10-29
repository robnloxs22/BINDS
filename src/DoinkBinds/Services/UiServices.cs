using DoinkBinds.Models;

namespace DoinkBinds.Services;

/// <summary>Modal dialogs and file pickers. Implemented in the UI layer (Views/DialogService).</summary>
public interface IDialogService
{
    Task<string?> PromptAsync(string title, string message, string initialValue = "", string confirmText = "OK");
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false);
    Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options, string? selected = null);
    Task ShowMessageAsync(string title, string message);
    Task<string?> PickOpenFileAsync(string title);
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName);
}

/// <summary>Window-level shell operations the view models need without referencing views.</summary>
public interface IShellService
{
    /// <summary>Shows/updates the small floating always-on-top status indicator.</summary>
    void ShowIndicator(AppStatus status, string text);
    void HideIndicator();
    void MinimizeMainWindow();
    void RestoreMainWindow();
    void OpenFolder(string path);
    void ApplyTheme(string theme);
}

/// <summary>Pop-up notifications raised by macros/rules (Notify steps). Implemented in the UI layer.</summary>
public interface INotificationService
{
    /// <summary>Thread-safe. Shows a floating notification that stays visible even when Doink Binds is minimised.</summary>
    void Notify(string title, string message, ToastKind kind = ToastKind.Info);
}

public enum ToastKind
{
    Info,
    Success,
    Error
}
