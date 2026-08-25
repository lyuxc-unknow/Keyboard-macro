using System;
using System.IO;
using System.Text.Json;
using SimToAutoWirte.Models;

namespace SimToAutoWirte.Services;

public sealed class MacroStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _fileLock = new();

    public MacroStorageService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        StoragePath = Path.Combine(localData, "SimToAutoWirte", "macros.json");
    }

    public string StoragePath { get; }

    public MacroStore? Load()
    {
        lock (_fileLock)
        {
            if (!File.Exists(StoragePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(StoragePath);
                return JsonSerializer.Deserialize<MacroStore>(json, JsonOptions);
            }
            catch (JsonException)
            {
                PreserveCorruptFile();
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
    }

    public void Save(MacroStore store)
    {
        lock (_fileLock)
        {
            var directory = Path.GetDirectoryName(StoragePath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = StoragePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(store, JsonOptions));
            File.Move(temporaryPath, StoragePath, overwrite: true);
        }
    }

    private void PreserveCorruptFile()
    {
        var backupPath = StoragePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(StoragePath, backupPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
