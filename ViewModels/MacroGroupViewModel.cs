using System;
using ReactiveUI;
using SimToAutoWirte.Models;

namespace SimToAutoWirte.ViewModels;

public sealed class MacroGroupViewModel : ViewModelBase
{
    private string _name;
    private string _macroText;
    private int _startDelay;
    private HotkeyOption _startHotkey;
    private HotkeyOption _stopHotkey;

    public MacroGroupViewModel(
        string name,
        string macroText,
        HotkeyOption startHotkey,
        HotkeyOption stopHotkey,
        int startDelay = 3,
        Guid? id = null)
    {
        Id = id is { } storedId && storedId != Guid.Empty ? storedId : Guid.NewGuid();
        _name = name;
        _macroText = macroText;
        _startHotkey = startHotkey;
        _stopHotkey = stopHotkey;
        _startDelay = Math.Clamp(startDelay, 0, 10);
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, string.IsNullOrWhiteSpace(value) ? "未命名宏" : value.Trim());
    }

    public string MacroText
    {
        get => _macroText;
        set
        {
            var nextValue = value ?? string.Empty;
            if (string.Equals(_macroText, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _macroText, nextValue);
            this.RaisePropertyChanged(nameof(CharacterCountText));
            this.RaisePropertyChanged(nameof(EstimatedDurationText));
        }
    }

    public int StartDelay
    {
        get => _startDelay;
        set => this.RaiseAndSetIfChanged(ref _startDelay, Math.Clamp(value, 0, 10));
    }

    public HotkeyOption StartHotkey
    {
        get => _startHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _startHotkey, value);
            this.RaisePropertyChanged(nameof(HotkeySummary));
        }
    }

    public HotkeyOption StopHotkey
    {
        get => _stopHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopHotkey, value);
            this.RaisePropertyChanged(nameof(HotkeySummary));
        }
    }

    public string CharacterCountText => $"{GetLogicalLength(MacroText):N0} 个字符";

    public string EstimatedDurationText
    {
        get
        {
            return "按键触发后立即替换";
        }
    }

    public string HotkeySummary => $"{StartHotkey.DisplayName} 开始  ·  {StopHotkey.DisplayName} 停止";

    public MacroGroupViewModel Clone(string name) => new(
        name,
        MacroText,
        StartHotkey,
        StopHotkey,
        StartDelay);

    private static int GetLogicalLength(string text) => text.Replace("\r\n", "\n").Length;
}
