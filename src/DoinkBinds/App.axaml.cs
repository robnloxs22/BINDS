using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.ViewModels;
using DoinkBinds.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DoinkBinds;

public partial class App : Application
{
    /// <summary>The DI container. Exposed for diagnostics; prefer constructor injection.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Composition root: every service is registered here, so swapping an implementation
    /// (e.g. a new platform backend) is a one-line change.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Infrastructure
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPlatformServices>(_ => PlatformFactory.Create());

        // Core services
        services.AddSingleton<IMacroLibraryService, MacroLibraryService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IMousePositionService, MousePositionService>();
        services.AddSingleton<IMacroRecorderService, MacroRecorderService>();
        services.AddSingleton<IMacroPlayerService, MacroPlayerService>();

        // UI-layer services
        services.AddSingleton<IPinPointPicker, PinPointPicker>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, ShellService>();

        // View models
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var provider = ConfigureServices();
        Services = provider;
        var log = provider.GetRequiredService<ILogService>();

        // Keep the app alive if a UI handler throws; log and show a toast instead of crashing.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
            provider.GetService<MainWindowViewModel>()?.Toast($"Something went wrong: {e.Exception.Message}", ToastKind.Error);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            window.Opened += (_, _) => vm.Initialize();
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            desktop.Exit += (_, _) =>
            {
                vm.Dispose();
                provider.Dispose();
            };

            log.Info($"Doink Binds started on {Environment.OSVersion} — data: {AppPaths.DataRoot}");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
