using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.ViewModels;

namespace DoinkBinds.Tests;

public sealed class FakeDialogs : IDialogService
{
    public Task<string?> PromptAsync(string title, string message, string initialValue = "", string confirmText = "OK") => Task.FromResult<string?>(initialValue);
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false) => Task.FromResult(true);
    public Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options, string? selected = null) => Task.FromResult(selected);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    public Task<string?> PickOpenFileAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> PickAnyFileAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult<string?>(null);
}

public sealed class FakeLibrary : ILibraryActions
{
    public List<string> Toasts { get; } = new();
    public Task RenameMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task DuplicateMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task DeleteMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task MoveMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task ExportMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public void PlayMacro(MacroViewModel macro) { }
    public Task RenameFolderAsync(FolderViewModel folder) => Task.CompletedTask;
    public Task DeleteFolderAsync(FolderViewModel folder) => Task.CompletedTask;
    public void NewMacroInFolder(FolderViewModel folder) { }
    public void MacroChanged(MacroViewModel macro) { }
    public void RefreshTriggers() { }
    public void Toast(string message, ToastKind kind = ToastKind.Info) => Toasts.Add(message);
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; set; }
    public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
    public Task<string?> GetTextAsync() => Task.FromResult(Text);
}

public class EditorTests
{
    private static MacroViewModel Editor(Macro? macro = null, FakeClipboard? clipboard = null)
    {
        var h = new Harness();
        var ctx = new EditorContext(h.Positions, h.Hotkeys, h.Settings, new FakeDialogs(), new FakeLibrary())
        {
            Clipboard = clipboard ?? new FakeClipboard()
        };
        return new MacroViewModel(macro ?? new Macro { Name = "Test" }, ctx);
    }

    private static Macro Numbered(int n)
    {
        var m = new Macro { Name = "Numbered" };
        for (var i = 0; i < n; i++)
            m.Actions.Add(new MacroAction { Type = MacroActionType.Comment, Text = $"s{i}" });
        return m;
    }

    private static string Order(MacroViewModel vm) => string.Join(",", vm.Model.Actions.Select(a => a.Text));

    [Fact]
    public void AddIf_AddsEndIf_AndUndoRedoRestores()
    {
        var vm = Editor();
        vm.AddStepCommand.Execute(nameof(MacroActionType.If));
        Assert.Equal(new[] { MacroActionType.If, MacroActionType.EndIf }, vm.Model.Actions.Select(a => a.Type));

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Model.Actions);
        Assert.Empty(vm.Steps);

        vm.RedoCommand.Execute(null);
        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal(new[] { 0, 0 }, vm.Steps.Select(s => s.Indent));
    }

    [Fact]
    public void AddLoop_WrapsMultiSelection_AndIndents()
    {
        var vm = Editor(Numbered(4));
        vm.SetSelection(new[] { vm.Steps[1], vm.Steps[2] });
        vm.AddStepCommand.Execute(nameof(MacroActionType.Loop));

        Assert.Equal(new[] { MacroActionType.Comment, MacroActionType.Loop, MacroActionType.Comment, MacroActionType.Comment, MacroActionType.EndLoop, MacroActionType.Comment },
            vm.Model.Actions.Select(a => a.Type));
        Assert.Equal(new[] { 0, 0, 1, 1, 0, 0 }, vm.Steps.Select(s => s.Indent));
        Assert.Null(vm.StructureWarning);
    }

    [Fact]
    public void MoveSteps_DragDown_And_Up()
    {
        var vm = Editor(Numbered(5));
        // Drag s0,s1 to before s4 (index 4)
        vm.MoveSteps(new[] { vm.Steps[0], vm.Steps[1] }, 4);
        Assert.Equal("s2,s3,s0,s1,s4", Order(vm));

        // Drag s4 to the top
        vm.MoveSteps(new[] { vm.Steps[4] }, 0);
        Assert.Equal("s4,s2,s3,s0,s1", Order(vm));

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Equal("s0,s1,s2,s3,s4", Order(vm));
    }

    [Fact]
    public async Task CopyPaste_InsertsClonesAfterSelection()
    {
        var clip = new FakeClipboard();
        var vm = Editor(Numbered(3), clip);
        vm.SetSelection(new[] { vm.Steps[0], vm.Steps[1] });
        vm.SelectedStep = vm.Steps[0];
        await vm.CopyStepsCommand.ExecuteAsync(null);
        Assert.Contains("doink-steps", clip.Text);

        vm.SetSelection(Array.Empty<ActionViewModel>());
        vm.SelectedStep = vm.Steps[2];
        await vm.PasteStepsCommand.ExecuteAsync(null);
        Assert.Equal("s0,s1,s2,s0,s1", Order(vm));
        Assert.NotSame(vm.Model.Actions[0], vm.Model.Actions[3]);

        // Paste works across editors through the system clipboard.
        var other = Editor(clipboard: clip);
        await other.PasteStepsCommand.ExecuteAsync(null);
        Assert.Equal("s0,s1", Order(other));
    }

    [Fact]
    public void BrokenStructure_ShowsWarning()
    {
        var m = Numbered(1);
        m.Actions.Add(new MacroAction { Type = MacroActionType.EndIf });
        var vm = Editor(m);
        _ = vm.Steps;
        Assert.Contains("End If", vm.StructureWarning);
    }

    [Theory]
    [InlineData(new[] { MacroActionType.If, MacroActionType.Else, MacroActionType.EndIf }, true)]
    [InlineData(new[] { MacroActionType.Loop, MacroActionType.If, MacroActionType.Break, MacroActionType.EndIf, MacroActionType.EndLoop }, true)]
    [InlineData(new[] { MacroActionType.If, MacroActionType.EndLoop }, false)]
    [InlineData(new[] { MacroActionType.Else }, false)]
    [InlineData(new[] { MacroActionType.Break }, false)]
    [InlineData(new[] { MacroActionType.Loop }, false)]
    [InlineData(new[] { MacroActionType.If, MacroActionType.Else, MacroActionType.Else, MacroActionType.EndIf }, false)]
    public void BlockMap_ValidatesStructure(MacroActionType[] types, bool valid)
    {
        var actions = types.Select(t => new MacroAction { Type = t }).ToList();
        var ex = Record.Exception(() => MacroPlayerService.BlockMap.Build(actions));
        Assert.Equal(valid, ex is null);
        if (!valid) Assert.IsType<PlaybackException>(ex);
    }

    [Fact]
    public void BlockMap_LinksBlocks()
    {
        var types = new[] { MacroActionType.Loop, MacroActionType.If, MacroActionType.Break, MacroActionType.Else, MacroActionType.EndIf, MacroActionType.EndLoop };
        var map = MacroPlayerService.BlockMap.Build(types.Select(t => new MacroAction { Type = t }).ToList());
        Assert.Equal(5, map.EndOf[0]);
        Assert.Equal(3, map.ElseOf[1]);
        Assert.Equal(4, map.EndOf[1]);
        Assert.Equal(4, map.EndOf[3]);
        Assert.Equal(0, map.StartOf[5]);
        Assert.Equal(0, map.LoopOfBreak[2]);
    }
}
