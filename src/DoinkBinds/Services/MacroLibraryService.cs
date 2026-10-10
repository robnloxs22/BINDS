using System.Text.Json;
using DoinkBinds.Models;

namespace DoinkBinds.Services;

/// <summary>Persists macros as individual .doink JSON files, one sub-directory per folder.</summary>
public interface IMacroLibraryService
{
    /// <summary>All loaded macros (in-memory cache).</summary>
    IReadOnlyList<Macro> Macros { get; }

    /// <summary>All folder names ("" = root folder, always present).</summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>Loads everything from disk. Unreadable files are skipped and reported in <paramref name="errors"/>.</summary>
    void Load(out List<string> errors);

    void Save(Macro macro);
    void Delete(Macro macro);
    Macro Duplicate(Macro macro);
    void MoveToFolder(Macro macro, string folder);

    string CreateFolder(string name);
    string RenameFolder(string oldName, string newName);

    /// <summary>Deletes a folder. Macros inside are moved to the root folder (never silently deleted).</summary>
    void DeleteFolder(string name);

    void Export(Macro macro, string filePath);

    /// <summary>Reads a .doink file and adds it to the library (with a fresh Id) in <paramref name="folder"/>.</summary>
    Macro Import(string filePath, string folder);

    /// <summary>Copies bundled sample macros into a "Samples" folder.</summary>
    int InstallSamples();
}

public sealed class MacroLibraryService : IMacroLibraryService
{
    private readonly ILogService _log;
    private readonly List<Macro> _macros = new();
    private readonly Dictionary<Guid, string> _paths = new();
    private readonly SortedSet<string> _folders = new(StringComparer.OrdinalIgnoreCase) { "" };

    public MacroLibraryService(ILogService log) => _log = log;

    public IReadOnlyList<Macro> Macros => _macros;
    public IReadOnlyList<string> Folders => _folders.ToList();

    public void Load(out List<string> errors)
    {
        errors = new List<string>();
        _macros.Clear();
        _paths.Clear();
        _folders.Clear();
        _folders.Add("");

        Directory.CreateDirectory(AppPaths.MacrosDirectory);

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.MacrosDirectory))
            _folders.Add(Path.GetFileName(dir));

        // Root folder + one level of sub-folders.
        var files = Directory.EnumerateFiles(AppPaths.MacrosDirectory, "*" + AppPaths.MacroExtension)
            .Concat(Directory.EnumerateDirectories(AppPaths.MacrosDirectory)
                .SelectMany(d => Directory.EnumerateFiles(d, "*" + AppPaths.MacroExtension)));

        foreach (var file in files)
        {
            try
            {
                var macro = ReadFile(file);
                var rel = Path.GetRelativePath(AppPaths.MacrosDirectory, Path.GetDirectoryName(file)!);
                macro.Folder = rel == "." ? "" : rel;

                // Two files with the same Id (e.g. copied by hand) — give the second one a new Id.
                if (_paths.ContainsKey(macro.Id)) macro.Id = Guid.NewGuid();

                _macros.Add(macro);
                _paths[macro.Id] = file;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                _log.Error($"Failed to load {file}", ex);
            }
        }
    }

    public void Save(Macro macro)
    {
        macro.ModifiedUtc = DateTime.UtcNow;
        var target = BuildPath(macro);

        // Name or folder changed → remove the old file after the new one is safely written.
        _paths.TryGetValue(macro.Id, out var oldPath);
        WriteFile(macro, target);
        if (oldPath is not null && !PathsEqual(oldPath, target) && File.Exists(oldPath))
            File.Delete(oldPath);

        _paths[macro.Id] = target;
        _folders.Add(macro.Folder);
        if (!_macros.Contains(macro)) _macros.Add(macro);
    }

    public void Delete(Macro macro)
    {
        if (_paths.TryGetValue(macro.Id, out var path) && File.Exists(path))
            File.Delete(path);
        _paths.Remove(macro.Id);
        _macros.Remove(macro);
    }

    public Macro Duplicate(Macro macro)
    {
        var copy = macro.Duplicate(UniqueName(macro.Name + " copy", macro.Folder));
        Save(copy);
        return copy;
    }

    public void MoveToFolder(Macro macro, string folder)
    {
        macro.Folder = folder;
        Save(macro);
    }

    public string CreateFolder(string name)
    {
        var clean = AppPaths.SanitizeName(name);
        var unique = clean;
        for (var i = 2; _folders.Contains(unique); i++) unique = $"{clean} {i}";
        Directory.CreateDirectory(Path.Combine(AppPaths.MacrosDirectory, unique));
        _folders.Add(unique);
        return unique;
    }

    public string RenameFolder(string oldName, string newName)
    {
        if (oldName.Length == 0) throw new InvalidOperationException("The root folder cannot be renamed.");
        var clean = AppPaths.SanitizeName(newName);
        if (string.Equals(clean, oldName, StringComparison.Ordinal)) return oldName;
        if (_folders.Contains(clean) && !string.Equals(clean, oldName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A folder named \"{clean}\" already exists.");

        var from = Path.Combine(AppPaths.MacrosDirectory, oldName);
        var to = Path.Combine(AppPaths.MacrosDirectory, clean);
        if (Directory.Exists(from))
        {
            if (string.Equals(oldName, clean, StringComparison.OrdinalIgnoreCase))
            {
                // Case-only rename on a case-insensitive file system needs a two-step move.
                var tmp = from + "_" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(from, tmp);
                Directory.Move(tmp, to);
            }
            else
            {
                Directory.Move(from, to);
            }
        }

        _folders.Remove(oldName);
        _folders.Add(clean);
        foreach (var m in _macros.Where(m => string.Equals(m.Folder, oldName, StringComparison.OrdinalIgnoreCase)))
        {
            m.Folder = clean;
            _paths[m.Id] = BuildPath(m);
        }
        return clean;
    }

    public void DeleteFolder(string name)
    {
        if (name.Length == 0) return;
        foreach (var m in _macros.Where(m => string.Equals(m.Folder, name, StringComparison.OrdinalIgnoreCase)).ToList())
            MoveToFolder(m, "");

        var dir = Path.Combine(AppPaths.MacrosDirectory, name);
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not delete folder {dir}", ex);
        }
        _folders.Remove(name);
    }

    public void Export(Macro macro, string filePath) => WriteFile(macro, filePath);

    public Macro Import(string filePath, string folder)
    {
        var macro = ReadFile(filePath);
        macro.Id = Guid.NewGuid();
        macro.Folder = folder;
        macro.TriggerHotkey = null; // avoid surprise conflicts with existing bindings
        macro.Name = UniqueName(macro.Name, folder);
        Save(macro);
        return macro;
    }

    public int InstallSamples()
    {
        if (!Directory.Exists(AppPaths.SamplesDirectory)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(AppPaths.SamplesDirectory, "*" + AppPaths.MacroExtension))
        {
            try
            {
                Import(file, "Samples");
                count++;
            }
            catch (Exception ex)
            {
                _log.Error($"Could not install sample {file}", ex);
            }
        }
        return count;
    }

    // ---------- helpers ----------

    private static Macro ReadFile(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<DoinkFile>(json, AppPaths.Json)
                   ?? throw new InvalidDataException("File is empty.");

        if (!string.Equals(file.Format, "doink", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not a Doink Binds macro file.");
        if (file.Version > DoinkFile.CurrentVersion)
            throw new InvalidDataException($"This macro was made with a newer version of Doink Binds (format v{file.Version}).");

        var macro = file.Macro ?? throw new InvalidDataException("File contains no macro.");
        macro.Actions ??= new List<MacroAction>();
        macro.Playback ??= new PlaybackSettings();
        macro.Playback.Speed = Math.Clamp(macro.Playback.Speed, 0.25, 3.0);
        macro.Playback.LoopCount = Math.Max(1, macro.Playback.LoopCount);
        macro.Name = string.IsNullOrWhiteSpace(macro.Name) ? Path.GetFileNameWithoutExtension(path) : macro.Name;
        foreach (var a in macro.Actions) a.DelayMs = Math.Max(0, a.DelayMs);
        return macro;
    }

    private static void WriteFile(Macro macro, string path)
    {
        var file = new DoinkFile { Macro = macro };
        AppPaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(file, AppPaths.Json));
    }

    /// <summary>"Name [1a2b3c4d].doink" — readable, yet unique even when two macros share a name.</summary>
    private static string BuildPath(Macro macro)
    {
        var dir = macro.Folder.Length == 0
            ? AppPaths.MacrosDirectory
            : Path.Combine(AppPaths.MacrosDirectory, AppPaths.SanitizeName(macro.Folder));
        var fileName = $"{AppPaths.SanitizeName(macro.Name)} [{macro.Id.ToString("N")[..8]}]{AppPaths.MacroExtension}";
        return Path.Combine(dir, fileName);
    }

    private string UniqueName(string baseName, string folder)
    {
        var name = baseName;
        for (var i = 2; _macros.Any(m => m.Folder == folder && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} ({i})";
        return name;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            // Windows and (default) macOS file systems are case-insensitive.
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
