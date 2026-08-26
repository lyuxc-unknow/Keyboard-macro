using System;
using System.Collections.Generic;

namespace SimToAutoWirte.Models;

public sealed class MacroStore
{
    public int Version { get; set; } = 2;

    public int SelectedMacroIndex { get; set; }

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public List<StoredMacro> Macros { get; set; } = [];
}

public sealed class StoredMacro
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "未命名宏";

    public string Content { get; set; } = string.Empty;

    public uint StartVirtualKey { get; set; }

    public uint StartModifiers { get; set; }

    public uint StopVirtualKey { get; set; }

    public uint StopModifiers { get; set; }

    public int StartDelay { get; set; } = 3;
}

internal sealed class MacroIndexStore
{
    public int Version { get; set; } = 2;

    public int SelectedMacroIndex { get; set; }

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public List<StoredMacroMetadata> Macros { get; set; } = [];
}

internal sealed class StoredMacroMetadata
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "未命名宏";

    public uint StartVirtualKey { get; set; }

    public uint StartModifiers { get; set; }

    public uint StopVirtualKey { get; set; }

    public uint StopModifiers { get; set; }

    public int StartDelay { get; set; } = 3;
}
