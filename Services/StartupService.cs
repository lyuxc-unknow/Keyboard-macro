using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SimToAutoWirte.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimToAutoWirte";

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var registeredCommand = key?.GetValue(ValueName) as string;
            return string.Equals(registeredCommand, BuildStartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("开机启动仅支持 Windows。");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");

        if (enabled)
        {
            key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序的启动路径。");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;

        // Framework-dependent DLLs may be launched by dotnet.exe rather than an apphost.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            return $"{Quote(processPath)} {Quote(entryAssemblyPath)} --silent";
        }

        return $"{Quote(processPath)} --silent";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
