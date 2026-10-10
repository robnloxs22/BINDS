using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DoinkBinds.ViewModels;

/// <summary>A folder node in the sidebar tree.</summary>
public sealed partial class FolderViewModel : ViewModelBase
{
    private readonly ILibraryActions _library;

    public FolderViewModel(string name, ILibraryActions library)
    {
        Name = name;
        _library = library;
        Macros.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Folder name on disk ("" = root).</summary>
    public string Name { get; }

    public bool IsRoot => Name.Length == 0;
    public string DisplayName => IsRoot ? "My Macros" : Name;
    public string CountText => Macros.Count.ToString();

    [ObservableProperty] private bool _isExpanded = true;

    public ObservableCollection<MacroViewModel> Macros { get; } = new();

    [RelayCommand] private void NewMacro() => _library.NewMacroInFolder(this);
    [RelayCommand] private Task Rename() => _library.RenameFolderAsync(this);
    [RelayCommand] private Task Delete() => _library.DeleteFolderAsync(this);
}
