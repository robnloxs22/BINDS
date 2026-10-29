using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;
using DoinkBinds.Vision;
using DoinkBinds.ViewModels;
using DoinkBinds.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DoinkBinds;

public partial class App : Application
{
    /// <summary>The DI container. Exposed for diagnostics; prefer constructor injection.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>True when a tray icon could be created (always on Windows/macOS).</summary>
    public static bool HasTray { get; private set; }

    public static void Notify(string title, string message) =>
        Services.GetService<INotificationService>()?.Notify(title, message);

    /// <summary>Tray / menu-bar icon: show the window, toggle macros/rules, stop, quit.</summary>
    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm)
    {
        try
        {
            void ShowWindow()
            {
                window.Show();
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Activate();
            }

            var show = new Avalonia.Controls.NativeMenuItem("Open Doink Binds");
            show.Click += (_, _) => ShowWindow();
            var macros = new Avalonia.Controls.NativeMenuItem("Macro hotkeys on/off") { Command = vm.ToggleMacrosFromTrayCommand };
            var rules = new Avalonia.Controls.NativeMenuItem("Watch screen (rules) on/off") { Command = vm.ToggleRulesFromTrayCommand };
            var stop = new Avalonia.Controls.NativeMenuItem("Stop playback") { Command = vm.StopFromTrayCommand };
            var quit = new Avalonia.Controls.NativeMenuItem("Quit");
            quit.Click += (_, _) =>
            {
                window.ReallyClose = true;
                desktop.Shutdown();
            };

            var menu = new Avalonia.Controls.NativeMenu();
            menu.Items.Add(show);
            menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
            menu.Items.Add(macros);
            menu.Items.Add(rules);
            menu.Items.Add(stop);
            menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
            menu.Items.Add(quit);

            using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://DoinkBinds/Assets/doink-256.png"));
            var tray = new Avalonia.Controls.TrayIcon
            {
                Icon = new Avalonia.Controls.WindowIcon(iconStream),
                ToolTipText = "Doink Binds",
                Menu = menu,
                IsVisible = true
            };
            tray.Clicked += (_, _) => ShowWindow();
            Avalonia.Controls.TrayIcon.SetIcons(this, new Avalonia.Controls.TrayIcons { tray });
            HasTray = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        }
        catch (Exception ex)
        {
            Services.GetService<ILogService>()?.Error("Tray icon unavailable", ex);
            HasTray = false;
        }
    }

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

        // Automation: screen reading, variables, rules, layouts, reports
        services.AddSingleton<IOcrEngine, PaddleOcrEngine>();
        services.AddSingleton<IScreenVision, ScreenVision>();
        services.AddSingleton<VariableStore>();
        services.AddSingleton<IAutomationStore, AutomationStore>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<ILayoutService, LayoutService>();
        services.AddSingleton<IRuleEngine, RuleEngine>();

        // UI-layer services
        services.AddSingleton<PinPointPicker>();
        services.AddSingleton<IPinPointPicker>(sp => sp.GetRequiredService<PinPointPicker>());
        services.AddSingleton<IScreenPicker>(sp => sp.GetRequiredService<PinPointPicker>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IStartupService, StartupService>();

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

            var startHidden = desktop.Args?.Contains("--minimized") == true || vm.StartMinimized;
            window.Opened += (_, _) =>
            {
                vm.Initialize();
                if (startHidden && HasTray) window.Hide();
            };
            CreateTrayIcon(desktop, window, vm);
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
