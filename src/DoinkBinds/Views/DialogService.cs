using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

/// <summary>Avalonia implementation of <see cref="IDialogService"/> (modal dialogs + native file pickers).</summary>
public sealed class DialogService : IDialogService
{
    private static readonly FilePickerFileType DoinkType = new("Doink macro")
    {
        Patterns = new[] { "*" + AppPaths.MacroExtension },
        MimeTypes = new[] { "application/json" },
        AppleUniformTypeIdentifiers = new[] { "public.json", "public.data" }
    };

    private static Window? Owner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static async Task<string?> Show(DialogWindow dialog)
    {
        var owner = Owner;
        if (owner is null) return null;
        return await dialog.ShowDialog<string?>(owner);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "", string confirmText = "OK") =>
        Show(new DialogWindow(DialogWindow.Kind.Prompt, title, message, confirmText, false, initialValue));

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false) =>
        await Show(new DialogWindow(DialogWindow.Kind.Confirm, title, message, confirmText, danger)) is not null;

    public Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options, string? selected = null) =>
        Show(new DialogWindow(DialogWindow.Kind.Choose, title, message, "Move", false, null, options, selected));

    public Task ShowMessageAsync(string title, string message) =>
        Show(new DialogWindow(DialogWindow.Kind.Message, title, message, "OK", false));

    public async Task<string?> PickOpenFileAsync(string title)
    {
        if (Owner?.StorageProvider is not { } sp) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { DoinkType, FilePickerFileTypes.All }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickAnyFileAsync(string title)
    {
        if (Owner?.StorageProvider is not { } sp) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName)
    {
        if (Owner?.StorageProvider is not { } sp) return null;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = AppPaths.MacroExtension.TrimStart('.'),
            FileTypeChoices = new[] { DoinkType },
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }
}
