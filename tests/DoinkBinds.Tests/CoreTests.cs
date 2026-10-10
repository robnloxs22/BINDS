using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;

namespace DoinkBinds.Tests;

public class MousePositionTests
{
    [Fact]
    public void Percentage_round_trips_on_secondary_monitor()
    {
        var h = new Harness();
        var point = new DesktopPoint(-1280, 520); // center of the left monitor
        var pos = h.Positions.FromAbsolute(point, MousePositionMode.Percentage, point);

        Assert.Equal(1, pos.MonitorIndex);
        Assert.InRange(pos.X, 49.9, 50.1);
        Assert.InRange(pos.Y, 49.9, 50.1);

        var back = h.Positions.Resolve(pos, default);
        Assert.InRange(back.X, -1281, -1279);
        Assert.InRange(back.Y, 519, 521);
    }

    [Fact]
    public void Percentage_falls_back_to_primary_when_monitor_is_unplugged()
    {
        var h = new Harness();
        var pos = new MousePosition { Mode = MousePositionMode.Percentage, X = 100, Y = 100, MonitorIndex = 5 };
        var p = h.Positions.Resolve(pos, default);
        Assert.Equal(1919, p.X, 3);
        Assert.Equal(1079, p.Y, 3);
    }

    [Fact]
    public void Relative_is_applied_to_current_cursor_and_clamped_to_screens()
    {
        var h = new Harness();
        var pos = new MousePosition { Mode = MousePositionMode.Relative, X = 50, Y = -20 };
        Assert.Equal(new DesktopPoint(150, 80), h.Positions.Resolve(pos, new DesktopPoint(100, 100)));

        // Way off to the right of the primary monitor → clamped onto it.
        var far = new MousePosition { Mode = MousePositionMode.Absolute, X = 99999, Y = 500 };
        Assert.Equal(new DesktopPoint(1919, 500), h.Positions.Resolve(far, default));
    }

    [Fact]
    public void Convert_preserves_target_point()
    {
        var h = new Harness();
        var abs = new MousePosition { Mode = MousePositionMode.Absolute, X = 960, Y = 540 };
        var pct = h.Positions.Convert(abs, MousePositionMode.Percentage, default);
        var rel = h.Positions.Convert(abs, MousePositionMode.Relative, new DesktopPoint(900, 500));

        Assert.Equal(MousePositionMode.Percentage, pct.Mode);
        Assert.InRange(h.Positions.Resolve(pct, default).X, 959.5, 960.5);
        Assert.Equal(60, rel.X);
        Assert.Equal(40, rel.Y);
    }

    [Fact]
    public void Virtual_desktop_percentage_spans_all_monitors()
    {
        var h = new Harness();
        h.Settings.Settings.MonitorAwareness = MonitorAwareness.VirtualDesktop;
        var pos = h.Positions.FromAbsolute(new DesktopPoint(-2560, -200), MousePositionMode.Percentage, default);
        Assert.Equal(MousePositionService.VirtualDesktopIndex, pos.MonitorIndex);
        Assert.Equal(0, pos.X);
        Assert.Equal(0, pos.Y);
    }
}

public class RecorderTests
{
    [Fact]
    public void Records_keys_and_clicks_with_timings_and_skips_hotkeys()
    {
        var h = new Harness();
        h.Settings.Settings.RecordMouseMovement = false;
        h.Recorder.Start();

        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 100, key: KeyCodes.A);
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, 50, key: KeyCodes.A);
        h.Platform.FakeHook.Raise(RawInputKind.MouseDown, 200, x: 10, y: 20);
        h.Platform.FakeHook.Raise(RawInputKind.MouseUp, 80, x: 10, y: 20);
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 10, key: KeyCodes.F(6)); // the stop hotkey

        var actions = h.Recorder.Stop();

        Assert.Equal(new[] { MacroActionType.KeyDown, MacroActionType.KeyUp, MacroActionType.MouseDown, MacroActionType.MouseUp },
            actions.Select(a => a.Type));
        Assert.Equal(50, actions[1].DelayMs);
        Assert.Equal(200, actions[2].DelayMs);
        Assert.Equal(80, actions[3].DelayMs);
        Assert.Equal(10, actions[2].Position!.X);
        Assert.Equal("notepad", actions[0].Window!.ProcessName);
    }

    [Fact]
    public void Ignores_synthetic_input_and_unbalanced_releases()
    {
        var h = new Harness();
        h.Settings.Settings.RecordMouseMovement = false;
        h.Recorder.Start();

        h.Platform.FakeHook.Raise(RawInputKind.MouseUp, 10, x: 5, y: 5);               // release of a pre-recording click
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, 10, key: 'B');              // release of a pre-recording key
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 10, key: 'C', synthetic: true); // our own playback
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 10, key: 'D');
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, 10, key: 'D');

        var actions = h.Recorder.Stop();
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal('D', a.KeyCode));
    }

    [Fact]
    public void Drops_modifiers_still_held_when_recording_stops()
    {
        var h = new Harness();
        h.Settings.Settings.RecordMouseMovement = false;
        h.Recorder.Start();

        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 10, key: 'X');
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, 10, key: 'X');
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, 10, key: KeyCodes.LControl); // Ctrl of Ctrl+F6
        var actions = h.Recorder.Stop();

        Assert.DoesNotContain(actions, a => a.KeyCode == KeyCodes.LControl);
    }

    [Fact]
    public void Ignores_input_while_own_window_is_focused()
    {
        var h = new Harness();
        h.Settings.Settings.RecordMouseMovement = false;
        h.Recorder.Start();
        h.Platform.FakeWindows.OwnForeground = true;
        h.Platform.FakeHook.Raise(RawInputKind.MouseDown, 10, x: 1, y: 1);
        h.Platform.FakeHook.Raise(RawInputKind.MouseUp, 10, x: 1, y: 1);
        Assert.Empty(h.Recorder.Stop());
    }

    [Fact]
    public void Throttles_mouse_moves_but_keeps_final_resting_point()
    {
        var h = new Harness();
        h.Settings.Settings.MouseMoveSampleMs = 50;
        h.Settings.Settings.MouseMoveMinDistance = 0;
        h.Platform.FakeInput.Cursor = new DesktopPoint(0, 0);
        h.Recorder.Start();

        for (var i = 1; i <= 10; i++)
            h.Platform.FakeHook.Raise(RawInputKind.MouseMove, 5, x: i * 10, y: 0); // every 5 ms
        h.Platform.FakeHook.Raise(RawInputKind.MouseDown, 100, x: 100, y: 0);
        h.Platform.FakeHook.Raise(RawInputKind.MouseUp, 10, x: 100, y: 0);

        var actions = h.Recorder.Stop();
        var moves = actions.Where(a => a.Type == MacroActionType.MouseMove).ToList();
        Assert.True(moves.Count < 6, $"expected throttling, got {moves.Count} moves");
        Assert.Equal(100, moves.Last().Position!.X); // the final point was flushed before the click
    }
}

public class PlayerTests
{
    private static Macro MakeMacro(params MacroAction[] actions) => new() { Name = "t", Actions = actions.ToList() };

    [Fact]
    public void Plays_steps_in_order_and_loops()
    {
        var h = new Harness();
        var macro = MakeMacro(
            new MacroAction { Type = MacroActionType.MouseClick, DelayMs = 5, Position = new MousePosition { X = 10, Y = 10 } },
            new MacroAction { Type = MacroActionType.KeyPress, DelayMs = 5, KeyCode = KeyCodes.A });

        var result = h.PlayAndWait(macro, new PlaybackSettings { Mode = PlaybackMode.LoopCount, LoopCount = 3, LoopDelayMs = 0, RestoreWindowFocus = false });

        Assert.True(result.Completed);
        Assert.Equal(3, result.IterationsCompleted);
        var log = h.Platform.FakeInput.Snapshot();
        Assert.Equal(3, log.Count(l => l == "Left down"));
        Assert.Equal(3, log.Count(l => l == "key 41 down"));
        Assert.True(log.IndexOf("move 10,10") < log.IndexOf("Left down"));
    }

    [Fact]
    public void Speed_scales_timing()
    {
        var h = new Harness();
        var macro = MakeMacro(new MacroAction { Type = MacroActionType.Wait, DelayMs = 400 });

        var sw = Stopwatch.StartNew();
        h.PlayAndWait(macro, new PlaybackSettings { Speed = 2.0, RestoreWindowFocus = false });
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 170, 380); // ~200 ms at 2×
    }

    [Fact]
    public void Stop_ends_infinite_loop_and_releases_held_keys()
    {
        var h = new Harness();
        var macro = MakeMacro(
            new MacroAction { Type = MacroActionType.KeyDown, DelayMs = 1, KeyCode = KeyCodes.LShift },
            new MacroAction { Type = MacroActionType.MouseDown, DelayMs = 1, Button = MacroMouseButton.Right },
            new MacroAction { Type = MacroActionType.Wait, DelayMs = 5000 });

        using var done = new ManualResetEventSlim();
        PlaybackResult? result = null;
        h.Player.Finished += (_, r) => { result = r; done.Set(); };
        Assert.True(h.Player.Play(macro, new PlaybackSettings { Mode = PlaybackMode.LoopInfinite, RestoreWindowFocus = false }));

        Thread.Sleep(150);
        h.Player.Stop();
        Assert.True(done.Wait(2000));

        Assert.True(result!.Cancelled);
        var log = h.Platform.FakeInput.Snapshot();
        Assert.Contains("key A0 up", log);
        Assert.Contains("Right up", log);
        Assert.Equal(AppStatus.Idle, h.Player.State);
    }

    [Fact]
    public void Pause_and_resume()
    {
        var h = new Harness();
        var macro = MakeMacro(
            new MacroAction { Type = MacroActionType.KeyPress, DelayMs = 100, KeyCode = KeyCodes.A },
            new MacroAction { Type = MacroActionType.KeyPress, DelayMs = 100, KeyCode = 'B' });

        using var done = new ManualResetEventSlim();
        h.Player.Finished += (_, _) => done.Set();
        h.Player.Play(macro, new PlaybackSettings { RestoreWindowFocus = false });
        h.Player.TogglePause();
        Assert.Equal(AppStatus.Paused, h.Player.State);
        Thread.Sleep(300);
        Assert.Empty(h.Platform.FakeInput.Snapshot()); // nothing happens while paused
        h.Player.TogglePause();
        Assert.True(done.Wait(2000));
        Assert.Contains("key 42 up", h.Platform.FakeInput.Snapshot());
    }

    [Fact]
    public void Wait_for_window_times_out_with_friendly_error()
    {
        var h = new Harness();
        h.Settings.Settings.WaitForWindowTimeoutMs = 200;
        var macro = MakeMacro(new MacroAction
        {
            Type = MacroActionType.KeyPress, KeyCode = KeyCodes.A,
            Window = new WindowTarget { ProcessName = "game", Title = "Game" }
        });

        var result = h.PlayAndWait(macro, new PlaybackSettings { WaitForWindow = true, RestoreWindowFocus = false });
        Assert.False(result.Completed);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public void Restore_focus_activates_recorded_window()
    {
        var h = new Harness();
        var macro = MakeMacro(new MacroAction
        {
            Type = MacroActionType.KeyPress, KeyCode = KeyCodes.A,
            Window = new WindowTarget { ProcessName = "game", Title = "Game" }
        });

        var result = h.PlayAndWait(macro, new PlaybackSettings { RestoreWindowFocus = true, WaitForWindow = true });
        Assert.True(result.Completed);
        Assert.Equal("game", h.Platform.FakeWindows.Foreground!.ProcessName);
    }
}

public class HotkeyTests
{
    [Fact]
    public void Fires_on_matching_combo_only_and_ignores_synthetic_and_repeat()
    {
        var h = new Harness();
        h.Settings.Settings.Hotkeys.First(b => b.Action == HotkeyAction.PlayPause).Hotkey = new Hotkey(KeyCodes.F(7), HotkeyModifiers.Control);
        var fired = new List<HotkeyAction>();
        h.Hotkeys.CommandTriggered += (_, a) => fired.Add(a);

        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(7));                  // no Ctrl → nothing
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.F(7));
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.LControl);
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(7));                  // fires
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(7));                  // auto-repeat → ignored
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.F(7));
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.LControl);
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(9), synthetic: true); // our playback → ignored

        Assert.Equal(new[] { HotkeyAction.PlayPause }, fired);
    }

    [Fact]
    public void Local_scope_requires_app_focus()
    {
        var h = new Harness();
        var binding = h.Settings.Settings.Hotkeys.First(b => b.Action == HotkeyAction.StopLoop);
        binding.Scope = HotkeyScope.Local;
        var count = 0;
        h.Hotkeys.CommandTriggered += (_, _) => count++;

        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(9));
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.F(9));
        Assert.Equal(0, count);

        h.Platform.FakeWindows.OwnForeground = true;
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(9));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Capture_returns_combo_and_escape_cancels()
    {
        var h = new Harness();
        var task = h.Hotkeys.CaptureAsync();
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.LShift);
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(10));
        var hk = await task;
        Assert.Equal(new Hotkey(KeyCodes.F(10), HotkeyModifiers.Shift), hk);
        Assert.Equal("Shift + F10", hk!.ToString());

        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.F(10));
        h.Platform.FakeHook.Raise(RawInputKind.KeyUp, key: KeyCodes.LShift);
        var cancel = h.Hotkeys.CaptureAsync();
        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.Escape);
        Assert.Null(await cancel);
    }

    [Fact]
    public void Macro_triggers_fire_and_conflicts_are_detected()
    {
        var h = new Harness();
        var id = Guid.NewGuid();
        h.Hotkeys.SetMacroTriggers(new[] { (id, new Hotkey(KeyCodes.F(10))) });
        Guid? fired = null;
        h.Hotkeys.MacroTriggered += (_, g) => fired = g;

        h.Platform.FakeHook.Raise(RawInputKind.KeyDown, key: KeyCodes.F(10));
        Assert.Equal(id, fired);
        Assert.True(h.Hotkeys.IsHotkeyKey(KeyCodes.F(10)));
        Assert.NotNull(h.Hotkeys.FindConflict(new Hotkey(KeyCodes.F(6))));
        Assert.Null(h.Hotkeys.FindConflict(new Hotkey(KeyCodes.F(11))));
    }
}

public class LibraryTests
{
    // All library tests share the module-level temp data folder, so each uses unique names.
    private static MacroLibraryService NewLibrary()
    {
        var lib = new MacroLibraryService(new NullLog());
        lib.Load(out _);
        return lib;
    }

    [Fact]
    public void Save_load_round_trip_preserves_everything()
    {
        var lib = NewLibrary();
        var macro = new Macro
        {
            Name = "Roundtrip " + Guid.NewGuid().ToString("N")[..6],
            Folder = "RT",
            TriggerHotkey = new Hotkey(KeyCodes.F(12), HotkeyModifiers.Alt),
            Playback = new PlaybackSettings { Mode = PlaybackMode.LoopCount, LoopCount = 7, Speed = 1.5 },
            Actions =
            {
                new MacroAction { Type = MacroActionType.MouseClick, DelayMs = 12, Position = new MousePosition { Mode = MousePositionMode.Percentage, X = 12.5, Y = 99, MonitorIndex = 1 } },
                new MacroAction { Type = MacroActionType.KeyDown, KeyCode = KeyCodes.Enter, Window = new WindowTarget { ProcessName = "app", Title = "T" } }
            }
        };
        lib.Save(macro);

        var reloaded = NewLibrary().Macros.Single(m => m.Id == macro.Id);
        Assert.Equal(macro.Name, reloaded.Name);
        Assert.Equal("RT", reloaded.Folder);
        Assert.Equal(macro.TriggerHotkey, reloaded.TriggerHotkey);
        Assert.Equal(PlaybackMode.LoopCount, reloaded.Playback.Mode);
        Assert.Equal(7, reloaded.Playback.LoopCount);
        Assert.Equal(1.5, reloaded.Playback.Speed);
        Assert.Equal(MousePositionMode.Percentage, reloaded.Actions[0].Position!.Mode);
        Assert.Equal(12.5, reloaded.Actions[0].Position!.X);
        Assert.Equal("app", reloaded.Actions[1].Window!.ProcessName);
    }

    [Fact]
    public void Rename_replaces_file_and_folder_ops_keep_macros()
    {
        var lib = NewLibrary();
        var folder = lib.CreateFolder("Ops " + Guid.NewGuid().ToString("N")[..6]);
        var macro = new Macro { Name = "Before", Folder = folder };
        lib.Save(macro);
        macro.Name = "After";
        lib.Save(macro);

        var files = Directory.GetFiles(Path.Combine(AppPaths.MacrosDirectory, folder));
        Assert.Single(files);
        Assert.Contains("After", Path.GetFileName(files[0]));

        var renamed = lib.RenameFolder(folder, folder + " X");
        Assert.Equal(renamed, macro.Folder);
        lib.DeleteFolder(renamed);
        Assert.Equal("", macro.Folder);
        Assert.Contains(NewLibrary().Macros, m => m.Id == macro.Id && m.Folder == "");
    }

    [Fact]
    public void Export_import_creates_independent_copy_and_rejects_bad_files()
    {
        var lib = NewLibrary();
        var macro = new Macro { Name = "Exportable", TriggerHotkey = new Hotkey(KeyCodes.F(5)) };
        macro.Actions.Add(new MacroAction { Type = MacroActionType.Wait, DelayMs = 10 });
        lib.Save(macro);

        var path = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.doink");
        lib.Export(macro, path);
        var imported = lib.Import(path, "");
        Assert.NotEqual(macro.Id, imported.Id);
        Assert.Null(imported.TriggerHotkey);
        Assert.Single(imported.Actions);
        Assert.NotEqual(macro.Name, imported.Name); // de-duplicated name

        var bad = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.doink");
        File.WriteAllText(bad, "{\"format\":\"doink\",\"version\":999,\"macro\":{}}");
        Assert.Throws<InvalidDataException>(() => lib.Import(bad, ""));
        File.WriteAllText(bad, "not json");
        Assert.ThrowsAny<Exception>(() => lib.Import(bad, ""));
    }

    [Fact]
    public void Bundled_samples_are_valid_doink_files()
    {
        var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
        var files = Directory.GetFiles(samples, "*.doink");
        Assert.NotEmpty(files);
        var lib = NewLibrary();
        foreach (var f in files)
        {
            var m = lib.Import(f, "SampleCheck");
            Assert.NotEmpty(m.Actions);
        }
    }
}
