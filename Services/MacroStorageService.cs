using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SimToAutoWirte.Models;

namespace SimToAutoWirte.Services;

public sealed class MacroStorageService
{
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _fileLock = new();
    private readonly Dictionary<Guid, string> _lastSavedContents = [];
    private string? _lastIndexJson;

    public MacroStorageService(string? storageDirectory = null)
    {
        StorageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimToAutoWirte");
        IndexPath = Path.Combine(StorageDirectory, "index.json");
        MacroContentDirectory = Path.Combine(StorageDirectory, "macros");
        LegacyStoragePath = Path.Combine(StorageDirectory, "macros.json");
        LegacyBackupPath = Path.Combine(StorageDirectory, "macros.v1.backup.json");
    }

    public string StorageDirectory { get; }

    public string IndexPath { get; }

    public string MacroContentDirectory { get; }

    public string LegacyStoragePath { get; }

    public string LegacyBackupPath { get; }

    // Kept as an alias for callers that previously inspected the single-file location.
    public string StoragePath => IndexPath;

    public MacroStore? Load()
    {
        lock (_fileLock)
        {
            if (File.Exists(IndexPath))
            {
                return LoadSplitStore();
            }

            if (File.Exists(LegacyStoragePath))
            {
                return LoadAndMigrateLegacyStore();
            }

            return null;
        }
    }

    public void Save(MacroStore store)
    {
        lock (_fileLock)
        {
            NormalizeStore(store);
            SaveCore(store);
        }
    }

    private MacroStore? LoadSplitStore()
    {
        try
        {
            var indexJson = File.ReadAllText(IndexPath, Utf8WithoutBom);
            var index = JsonSerializer.Deserialize<MacroIndexStore>(indexJson, JsonOptions);
            if (index is null)
            {
                return null;
            }

            var store = new MacroStore
            {
                Version = CurrentVersion,
                SelectedMacroIndex = index.SelectedMacroIndex,
                ThemeMode = index.ThemeMode,
            };

            foreach (var metadata in index.Macros)
            {
                var contentPath = GetMacroContentPath(metadata.Id);
                var content = File.Exists(contentPath)
                    ? File.ReadAllText(contentPath, Utf8WithoutBom)
                    : string.Empty;
                store.Macros.Add(new StoredMacro
                {
                    Id = metadata.Id,
                    Name = metadata.Name,
                    Content = content,
                    StartVirtualKey = metadata.StartVirtualKey,
                    StartModifiers = metadata.StartModifiers,
                    StopVirtualKey = metadata.StopVirtualKey,
                    StopModifiers = metadata.StopModifiers,
                    StartDelay = metadata.StartDelay,
                });
            }

            NormalizeStore(store);
            CacheLoadedStore(store);
            _lastIndexJson = indexJson;
            NormalizePersistedLineEndings(store);
            return store;
        }
        catch (JsonException)
        {
            PreserveCorruptFile(IndexPath);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private MacroStore? LoadAndMigrateLegacyStore()
    {
        MacroStore? store;
        try
        {
            var legacyJson = File.ReadAllText(LegacyStoragePath, Utf8WithoutBom);
            store = JsonSerializer.Deserialize<MacroStore>(legacyJson, JsonOptions);
        }
        catch (JsonException)
        {
            PreserveCorruptFile(LegacyStoragePath);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (store is null)
        {
            return null;
        }

        NormalizeStore(store);
        try
        {
            SaveCore(store);
            PreserveLegacyBackup();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory data remains usable; a later save can retry the migration.
            _lastSavedContents.Clear();
            _lastIndexJson = null;
        }

        return store;
    }

    private void SaveCore(MacroStore store)
    {
        Directory.CreateDirectory(StorageDirectory);
        Directory.CreateDirectory(MacroContentDirectory);

        var expectedIds = new HashSet<Guid>();
        foreach (var macro in store.Macros)
        {
            expectedIds.Add(macro.Id);
            var content = WindowsLineEndings.Normalize(macro.Content);
            macro.Content = content;
            if (_lastSavedContents.TryGetValue(macro.Id, out var previous)
                && (ReferenceEquals(previous, content)
                    || string.Equals(previous, content, StringComparison.Ordinal)))
            {
                continue;
            }

            WriteAllTextAtomically(GetMacroContentPath(macro.Id), content);
            _lastSavedContents[macro.Id] = content;
        }

        var indexJson = JsonSerializer.Serialize(CreateIndex(store), JsonOptions);
        if (!string.Equals(_lastIndexJson, indexJson, StringComparison.Ordinal)
            || !File.Exists(IndexPath))
        {
            WriteAllTextAtomically(IndexPath, indexJson);
            _lastIndexJson = indexJson;
        }

        DeleteOrphanedMacroFiles(expectedIds);
        foreach (var removedId in _lastSavedContents.Keys.Where(id => !expectedIds.Contains(id)).ToArray())
        {
            _lastSavedContents.Remove(removedId);
        }
    }

    private void DeleteOrphanedMacroFiles(HashSet<Guid> expectedIds)
    {
        foreach (var filePath in Directory.EnumerateFiles(MacroContentDirectory, "*.txt"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(filePath), "N", out var id)
                && !expectedIds.Contains(id))
            {
                File.Delete(filePath);
            }
        }
    }

    private void CacheLoadedStore(MacroStore store)
    {
        _lastSavedContents.Clear();
        foreach (var macro in store.Macros)
        {
            var contentPath = GetMacroContentPath(macro.Id);
            if (!File.Exists(contentPath))
            {
                continue;
            }

            // 只缓存磁盘中与规范化后的内存内容完全一致的正文。
            // 如果旧文件使用 LF/孤立 CR，LoadSplitStore 后虽已规范化内存，
            // 仍需让 SaveCore 重写磁盘文件为 Windows CRLF。
            var persistedContent = File.ReadAllText(contentPath, Utf8WithoutBom);
            if (string.Equals(persistedContent, macro.Content, StringComparison.Ordinal))
            {
                _lastSavedContents[macro.Id] = macro.Content;
            }
        }
    }

    private void NormalizePersistedLineEndings(MacroStore store)
    {
        try
        {
            SaveCore(store);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 正文已在内存中规范化；下次用户保存时再尝试修正磁盘文件。
            _lastSavedContents.Clear();
            _lastIndexJson = null;
        }
    }

    private static void NormalizeStore(MacroStore store)
    {
        store.Version = CurrentVersion;
        var usedIds = new HashSet<Guid>();
        foreach (var macro in store.Macros)
        {
            if (macro.Id == Guid.Empty || !usedIds.Add(macro.Id))
            {
                do
                {
                    macro.Id = Guid.NewGuid();
                }
                while (!usedIds.Add(macro.Id));
            }

            macro.Name = string.IsNullOrWhiteSpace(macro.Name) ? "未命名宏" : macro.Name;
            macro.Content = WindowsLineEndings.Normalize(macro.Content);
        }
    }

    private static MacroIndexStore CreateIndex(MacroStore store)
    {
        var index = new MacroIndexStore
        {
            Version = CurrentVersion,
            SelectedMacroIndex = store.SelectedMacroIndex,
            ThemeMode = store.ThemeMode,
        };

        foreach (var macro in store.Macros)
        {
            index.Macros.Add(new StoredMacroMetadata
            {
                Id = macro.Id,
                Name = macro.Name,
                StartVirtualKey = macro.StartVirtualKey,
                StartModifiers = macro.StartModifiers,
                StopVirtualKey = macro.StopVirtualKey,
                StopModifiers = macro.StopModifiers,
                StartDelay = macro.StartDelay,
            });
        }

        return index;
    }

    private string GetMacroContentPath(Guid id) => Path.Combine(MacroContentDirectory, $"{id:N}.txt");

    private static void WriteAllTextAtomically(string path, string content)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, Utf8WithoutBom);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void PreserveLegacyBackup()
    {
        try
        {
            File.Move(LegacyStoragePath, LegacyBackupPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void PreserveCorruptFile(string path)
    {
        var backupPath = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backupPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
