using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

/// <summary>System clipboard via the main window.</summary>
public sealed class ClipboardService : IClipboardService
{
    private static TopLevel? Top =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task SetTextAsync(string text)
    {
        if (Top?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync() =>
        Top?.Clipboard is { } cb ? await cb.TryGetTextAsync() : null;
}
