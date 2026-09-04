using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using ReactiveUI;
using SimToAutoWirte.Models;
using SimToAutoWirte.Services;

namespace SimToAutoWirte.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int FirstHotkeyId = 0x5101;

    private const string SampleCode = """
        using System;

        namespace Demo;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("Hello from the keyboard macro!");
            }
        }
        """;
    private const string WelcomeText = """
        欢迎来到输入演示台。
        按下这一组宏的开始快捷键，就可以在当前窗口逐字展示这段内容。
        """;

    private readonly KeyboardInputService _keyboardInputService = new();
    private readonly KeyboardInterceptionService _keyboardInterceptionService = new();
    private readonly MacroStorageService _macroStorageService = new();
    private readonly StartupService _startupService = new();
    private readonly SemaphoreSlim _hotkeyConfigurationLock = new(1, 1);
    private readonly object _replacementStateLock = new();
    private GlobalHotkeyService? _globalHotkeyService;
    private CancellationTokenSource? _runCancellation;
    private MacroGroupViewModel _selectedMacro;
    private MacroGroupViewModel? _activeMacro;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private bool _isStartWithWindowsEnabled;
    private bool _isRunning;
    private bool _isPaused;
    private double _progress;
    private string _statusTitle = "正在初始化";
    private string _statusDetail = "准备全局热键";
    private StatusSeverity _statusSeverity = StatusSeverity.Busy;
    private string _runningMacroName = string.Empty;
    private string _runningProgressDetail = string.Empty;
    private int _replacementIndex;
    private int _hotkeyConfigurationVersion;
    private CancellationTokenSource? _saveDebounce;
    private bool _disposed;

    public MainWindowViewModel()
    {
        HotkeyOptions = CreateHotkeyOptions();
        var stored = _macroStorageService.Load();
        var loadedMacros = LoadMacros(stored);
        Macros = new ObservableCollection<MacroGroupViewModel>(loadedMacros);
        _themeMode = stored?.ThemeMode ?? AppThemeMode.System;
        try
        {
            _isStartWithWindowsEnabled = _startupService.IsEnabled;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or SecurityException)
        {
            _isStartWithWindowsEnabled = false;
        }

        // 没有这个订阅，侧栏的"N 组宏"与删除按钮的可用性在增删后不会刷新。
        Macros.CollectionChanged += MacrosOnCollectionChanged;
        foreach (var macro in Macros)
        {
            SubscribeToMacro(macro);
        }

        _keyboardInterceptionService.ReplacementRequested = ReplaceActiveInputAsync;
        _keyboardInterceptionService.ReplacementFailed = exception => Dispatcher.UIThread.Post(() =>
        {
            if (IsRunning && !IsPaused)
            {
                SetStatus("替换输入失败", exception.Message, StatusSeverity.Error);
            }
        });

        var selectedIndex = Math.Clamp(stored?.SelectedMacroIndex ?? 0, 0, Macros.Count - 1);
        _selectedMacro = Macros[selectedIndex];
        ApplyTheme();
    }

    public ObservableCollection<MacroGroupViewModel> Macros { get; }

    public IReadOnlyList<HotkeyOption> HotkeyOptions { get; }

    public MacroGroupViewModel SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (ReferenceEquals(_selectedMacro, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedMacro, value);
            this.RaisePropertyChanged(nameof(CanStart));
            ScheduleSave();
        }
    }

    public MacroGroupViewModel? ActiveMacro
    {
        get => _activeMacro;
        private set => this.RaiseAndSetIfChanged(ref _activeMacro, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanStart));
            this.RaisePropertyChanged(nameof(CanStop));
            this.RaisePropertyChanged(nameof(CanConfigure));
            this.RaisePropertyChanged(nameof(CanRemoveMacro));
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            this.RaisePropertyChanged(nameof(CanStart));
            this.RaisePropertyChanged(nameof(StartActionLabel));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            this.RaiseAndSetIfChanged(ref _progress, value);
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Progress:0}%";

    public string StatusTitle
    {
        get => _statusTitle;
        private set => this.RaiseAndSetIfChanged(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => this.RaiseAndSetIfChanged(ref _statusDetail, value);
    }

    /// <summary>
    /// 状态的严重级别。界面用它给指示灯与状态文案上语义色，
    /// 而不是像旧版那样恒定显示绿色。
    /// </summary>
    public StatusSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set
        {
            this.RaiseAndSetIfChanged(ref _statusSeverity, value);
            this.RaisePropertyChanged(nameof(IsStatusOk));
            this.RaisePropertyChanged(nameof(IsStatusBusy));
            this.RaisePropertyChanged(nameof(IsStatusWarning));
            this.RaisePropertyChanged(nameof(IsStatusError));
        }
    }

    // 供 XAML 的 Classes.xxx 绑定使用，这样颜色可以留在主题令牌里，
    // 明暗切换时自动跟随。
    public bool IsStatusOk => StatusSeverity == StatusSeverity.Ready;

    public bool IsStatusBusy => StatusSeverity == StatusSeverity.Busy;

    public bool IsStatusWarning => StatusSeverity == StatusSeverity.Warning;

    public bool IsStatusError => StatusSeverity == StatusSeverity.Error;

    /// <summary>运行浮层显示的宏名称。运行结束后保留最后一次的值，避免绑定闪烁。</summary>
    public string RunningMacroName
    {
        get => _runningMacroName;
        private set => this.RaiseAndSetIfChanged(ref _runningMacroName, value);
    }

    /// <summary>运行浮层显示的"已发送 / 总数"文案。</summary>
    public string RunningProgressDetail
    {
        get => _runningProgressDetail;
        private set => this.RaiseAndSetIfChanged(ref _runningProgressDetail, value);
    }

    public AppThemeMode ThemeMode
    {
        get => _themeMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _themeMode, value);
            this.RaisePropertyChanged(nameof(ThemeModeGlyph));
            this.RaisePropertyChanged(nameof(ThemeModeLabel));
        }
    }

    public string ThemeModeGlyph => ThemeMode switch
    {
        AppThemeMode.Light => "☀",
        AppThemeMode.Dark => "☾",
        _ => "◐",
    };

    public string ThemeModeLabel => ThemeMode switch
    {
        AppThemeMode.Light => "亮色",
        AppThemeMode.Dark => "暗色",
        _ => "跟随系统",
    };

    public string MacroCountText => $"{Macros.Count} 组宏";

    public bool IsStartWithWindowsEnabled
    {
        get => _isStartWithWindowsEnabled;
        set
        {
            if (_isStartWithWindowsEnabled == value)
            {
                return;
            }

            try
            {
                _startupService.SetEnabled(value);
                this.RaiseAndSetIfChanged(ref _isStartWithWindowsEnabled, value);
                SetStatus(
                    value ? "已开启开机启动" : "已关闭开机启动",
                    value ? "下次登录 Windows 时将在后台静默运行" : "已从当前用户启动项中移除",
                    StatusSeverity.Ready);
            }
            catch (Exception exception) when (exception is System.IO.IOException
                                                or UnauthorizedAccessException
                                                or SecurityException
                                                or InvalidOperationException
                                                or PlatformNotSupportedException)
            {
                this.RaisePropertyChanged();
                SetStatus("无法修改开机启动", exception.Message, StatusSeverity.Error);
            }
        }
    }

    public bool CanStart => IsPaused || (!IsRunning && SelectedMacro.MacroText.Length > 0);

    public bool CanStop => IsRunning;

    public bool CanConfigure => !IsRunning;

    public bool CanRemoveMacro => !IsRunning && Macros.Count > 1;

    public string StartActionLabel => IsPaused ? "▶  继续" : "▶  开始";

    /// <summary>在 跟随系统 → 亮色 → 暗色 之间循环切换。</summary>
    public void CycleThemeMode()
    {
        ThemeMode = ThemeMode switch
        {
            AppThemeMode.System => AppThemeMode.Light,
            AppThemeMode.Light => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        };

        ApplyTheme();
        ScheduleSave();
    }

    public void AddMacro()
    {
        if (!CanConfigure)
        {
            return;
        }

        var available = GetUnusedHotkeys().Take(2).ToArray();
        if (available.Length < 2)
        {
            SetStatus("无法新建", "可用快捷键不足，请先修改或释放已有快捷键。", StatusSeverity.Warning);
            return;
        }

        var macro = new MacroGroupViewModel(
            $"宏 {Macros.Count + 1}",
            string.Empty,
            available[0],
            available[1]);
        Macros.Add(macro);
        SelectedMacro = macro;
        SetStatus("已新建宏组", macro.HotkeySummary, StatusSeverity.Ready);
    }

    public void DuplicateSelectedMacro()
    {
        if (!CanConfigure)
        {
            return;
        }

        var available = GetUnusedHotkeys().Take(2).ToArray();
        if (available.Length < 2)
        {
            SetStatus("无法复制", "可用快捷键不足，请先修改或释放已有快捷键。", StatusSeverity.Warning);
            return;
        }

        var copy = SelectedMacro.Clone($"{SelectedMacro.Name} 副本");
        copy.StartHotkey = available[0];
        copy.StopHotkey = available[1];
        var index = Macros.IndexOf(SelectedMacro);
        Macros.Insert(index + 1, copy);
        SelectedMacro = copy;
        SetStatus("已复制宏组", copy.HotkeySummary, StatusSeverity.Ready);
    }

    public void RemoveSelectedMacro()
    {
        if (!CanRemoveMacro)
        {
            return;
        }

        var index = Macros.IndexOf(SelectedMacro);
        Macros.Remove(SelectedMacro);
        SelectedMacro = Macros[Math.Min(index, Macros.Count - 1)];
        SetStatus("已删除宏组", SelectedMacro.HotkeySummary, StatusSeverity.Ready);
    }

    public async Task ConfigureHotkeysAsync()
    {
        if (_disposed || IsRunning)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _hotkeyConfigurationVersion);
        await _hotkeyConfigurationLock.WaitAsync();
        try
        {
            if (_disposed || IsRunning || requestVersion != Volatile.Read(ref _hotkeyConfigurationVersion))
            {
                return;
            }

            var hookResult = await _keyboardInterceptionService.StartAsync();
            if (!hookResult.IsSuccessful)
            {
                SetStatus("键盘拦截不可用", hookResult.ErrorMessage, StatusSeverity.Error);
                return;
            }

            var previousService = _globalHotkeyService;
            _globalHotkeyService = null;
            if (previousService is not null)
            {
                await previousService.StopAsync();
            }

            if (requestVersion != Volatile.Read(ref _hotkeyConfigurationVersion))
            {
                return;
            }

            var bindings = new List<GlobalHotkeyBinding>(Macros.Count * 2);
            var hotkeyId = FirstHotkeyId;
            foreach (var macro in Macros)
            {
                var macroForCallback = macro;
                bindings.Add(new GlobalHotkeyBinding(
                    hotkeyId++,
                    macroForCallback.StartHotkey,
                    () => Dispatcher.UIThread.Post(() => _ = StartOrResumeMacroAsync(macroForCallback, useCountdown: false))));
                bindings.Add(new GlobalHotkeyBinding(
                    hotkeyId++,
                    macroForCallback.StopHotkey,
                    () => Dispatcher.UIThread.Post(PauseOrStop)));
            }

            var nextService = new GlobalHotkeyService();
            var result = await nextService.RegisterAsync(bindings);
            if (requestVersion != Volatile.Read(ref _hotkeyConfigurationVersion))
            {
                await nextService.StopAsync();
                return;
            }

            _globalHotkeyService = nextService;
            if (result.IsSuccessful)
            {
                SetStatus(
                    "就绪",
                    $"已注册 {Macros.Count} 组热键 · 当前 {SelectedMacro.Name}",
                    StatusSeverity.Ready);
            }
            else
            {
                SetStatus("热键不可用", result.ErrorMessage, StatusSeverity.Error);
            }
        }
        finally
        {
            _hotkeyConfigurationLock.Release();
        }
    }

    public Task StartAsync(bool useCountdown) => StartOrResumeMacroAsync(SelectedMacro, useCountdown);

    /// <summary>运行中第一次调用暂停；已暂停时再次调用才完全停止。</summary>
    public void PauseOrStop()
    {
        lock (_replacementStateLock)
        {
            if (!IsRunning)
            {
                return;
            }

            // 倒计时阶段还没有开启键盘拦截，此时沿用原有行为直接取消。
            if (IsPaused || !_keyboardInterceptionService.IsEnabled)
            {
                Stop();
                return;
            }

            IsPaused = true;
            _keyboardInterceptionService.SetEnabled(false);
            var macro = ActiveMacro;
            if (macro is not null)
            {
                var logicalLength = GetLogicalLength(macro.MacroText);
                var completed = GetCompletedLogicalLength(macro.MacroText);
                SetStatus(
                    $"替换已暂停 · {macro.Name}",
                    $"按 {macro.StartHotkey.DisplayName} 继续，或再次按 {macro.StopHotkey.DisplayName} 停止",
                    StatusSeverity.Warning);
                RunningProgressDetail = $"已暂停 · 已替换 {completed:N0} / {logicalLength:N0} 字符";
            }
        }
    }

    public void Stop()
    {
        lock (_replacementStateLock)
        {
            _keyboardInterceptionService.SetEnabled(false);
            _runCancellation?.Cancel();
        }
    }

    public void LoadSample() => SelectedMacro.MacroText = SampleCode;

    private Task StartOrResumeMacroAsync(MacroGroupViewModel macro, bool useCountdown)
    {
        if (IsPaused && ReferenceEquals(ActiveMacro, macro))
        {
            Resume();
            return Task.CompletedTask;
        }

        return StartMacroAsync(macro, useCountdown);
    }

    private void Resume()
    {
        lock (_replacementStateLock)
        {
            var macro = ActiveMacro;
            if (!IsRunning || !IsPaused || macro is null)
            {
                return;
            }

            IsPaused = false;
            _keyboardInterceptionService.SetEnabled(true);
            var logicalLength = GetLogicalLength(macro.MacroText);
            var completed = GetCompletedLogicalLength(macro.MacroText);
            SetStatus(
                $"拦截已继续 · {macro.Name}",
                $"每次普通按键替换 1 个字符 · 共 {logicalLength:N0} 个字符 · F1-F12、退格键放行",
                StatusSeverity.Busy);
            RunningProgressDetail = $"已替换 {completed:N0} / {logicalLength:N0} 字符";
        }
    }

    private async Task StartMacroAsync(MacroGroupViewModel macro, bool useCountdown)
    {
        if (_disposed || IsRunning || macro.MacroText.Length == 0)
        {
            return;
        }

        SelectedMacro = macro;
        ActiveMacro = macro;
        RunningMacroName = macro.Name;
        RunningProgressDetail = string.Empty;
        Progress = 0;
        _replacementIndex = 0;
        IsPaused = false;
        IsRunning = true;
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;

        try
        {
            if (useCountdown)
            {
                for (var remaining = macro.StartDelay; remaining > 0; remaining--)
                {
                    SetStatus(
                        $"{remaining} 秒后开始 · {macro.Name}",
                        $"按 {macro.StopHotkey.DisplayName} 可取消",
                        StatusSeverity.Busy);
                    RunningProgressDetail = $"{remaining} 秒后开始";
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                }
            }

            _keyboardInterceptionService.SetEnabled(true);
            var logicalLength = GetLogicalLength(macro.MacroText);
            SetStatus(
                $"拦截已开启 · {macro.Name}",
                $"每次普通按键替换 1 个字符 · 共 {logicalLength:N0} 个字符 · F1-F12、退格键放行",
                StatusSeverity.Busy);
            RunningProgressDetail = "等待键盘输入";

            // 拦截模式持续到暂停或停止热键触发；每次普通按键由后台 worker 输出下一个字符。
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"拦截已停止 · {macro.Name}", "普通键盘输入已恢复", StatusSeverity.Warning);
        }
        catch (Exception exception)
        {
            SetStatus("输入失败", exception.Message, StatusSeverity.Error);
        }
        finally
        {
            _keyboardInterceptionService.SetEnabled(false);
            if (ReferenceEquals(_runCancellation, cancellation))
            {
                _runCancellation = null;
            }

            cancellation.Dispose();
            ActiveMacro = null;
            IsPaused = false;
            IsRunning = false;
        }
    }

    /// <summary>低级钩子每拦截一个普通按键只输出宏文本的下一个字符。</summary>
    private Task ReplaceActiveInputAsync()
    {
        lock (_replacementStateLock)
        {
            var macro = ActiveMacro;
            var cancellation = _runCancellation;
            if (macro is null || cancellation is null || !IsRunning || IsPaused || macro.MacroText.Length == 0)
            {
                return Task.CompletedTask;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            var text = macro.MacroText;
            var logicalLength = GetLogicalLength(text);
            var index = _replacementIndex;
            if (index >= text.Length)
            {
                Stop();
                return Task.CompletedTask;
            }

            var character = text[index];
            var isNewLine = character is '\r' or '\n';
            var advance = character == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
                ? 2
                : 1;

            // 必须先发送、成功后再推进索引。SendInput 会失败（目标窗口以管理员权限运行被 UIPI 拦截、
            // 前台窗口正在切换等），此时注入方法抛异常。旧实现无论成败都先推进索引，
            // 失败的那个字符就被永久跳过；一旦跳过的是换行符，后面的内容会挤到同一行，
            // 表现为"有时莫名其妙不换行、代码错乱"。现在失败则索引原地不动，下次按键重试同一个字符。
            if (isNewLine)
            {
                _keyboardInputService.SendNewLine();
            }
            else
            {
                _keyboardInputService.SendCharacter(character);
            }
            _replacementIndex = index + advance;

            var completed = _replacementIndex >= text.Length ? logicalLength : GetLogicalLength(text[.._replacementIndex]);
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(ActiveMacro, macro) && IsRunning && !IsPaused)
                {
                    Progress = logicalLength == 0 ? 100 : completed * 100d / logicalLength;
                    RunningProgressDetail = $"已替换 {completed:N0} / {logicalLength:N0} 字符";
                }
            });

            if (_replacementIndex >= text.Length)
            {
                Stop();
            }
        }

        return Task.CompletedTask;
    }

    private int GetCompletedLogicalLength(string text) =>
        _replacementIndex >= text.Length ? GetLogicalLength(text) : GetLogicalLength(text[.._replacementIndex]);

    private void SetStatus(string title, string detail, StatusSeverity severity)
    {
        StatusTitle = title;
        StatusDetail = detail;
        StatusSeverity = severity;
    }

    private void MacrosOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MacroGroupViewModel macro in e.OldItems)
            {
                UnsubscribeFromMacro(macro);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MacroGroupViewModel macro in e.NewItems)
            {
                SubscribeToMacro(macro);
            }
        }

        this.RaisePropertyChanged(nameof(MacroCountText));
        this.RaisePropertyChanged(nameof(CanRemoveMacro));
        ScheduleSave();
    }

    private IEnumerable<HotkeyOption> GetUnusedHotkeys()
    {
        var used = Macros
            .SelectMany(macro => new[] { macro.StartHotkey, macro.StopHotkey })
            .ToHashSet();
        return HotkeyOptions.Where(option => !used.Contains(option));
    }

    private void SubscribeToMacro(MacroGroupViewModel macro) => macro.PropertyChanged += SelectedMacroOnPropertyChanged;

    private void UnsubscribeFromMacro(MacroGroupViewModel macro) => macro.PropertyChanged -= SelectedMacroOnPropertyChanged;

    private void SelectedMacroOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedMacro)
            && e.PropertyName == nameof(MacroGroupViewModel.MacroText))
        {
            this.RaisePropertyChanged(nameof(CanStart));
        }

        if (e.PropertyName is nameof(MacroGroupViewModel.CharacterCountText)
            or nameof(MacroGroupViewModel.EstimatedDurationText)
            or nameof(MacroGroupViewModel.HotkeySummary))
        {
            return;
        }

        ScheduleSave();
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = ThemeMode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private List<MacroGroupViewModel> LoadMacros(MacroStore? store)
    {
        if (store?.Macros is not { Count: > 0 })
        {
            return
            [
                new("C# 示例", SampleCode, GetHotkey("F8"), GetHotkey("F9")),
                new("欢迎词", WelcomeText, GetHotkey("Ctrl+F8"), GetHotkey("Ctrl+F9"), 2),
            ];
        }

        var result = new List<MacroGroupViewModel>(store.Macros.Count);
        foreach (var storedMacro in store.Macros)
        {
            var startHotkey = ResolveHotkey(storedMacro.StartVirtualKey, storedMacro.StartModifiers)
                ?? GetHotkey("F8");
            var stopHotkey = ResolveHotkey(storedMacro.StopVirtualKey, storedMacro.StopModifiers)
                ?? GetHotkey("F9");
            result.Add(new MacroGroupViewModel(
                storedMacro.Name ?? "未命名宏",
                storedMacro.Content ?? string.Empty,
                startHotkey,
                stopHotkey,
                storedMacro.StartDelay,
                storedMacro.Id));
        }

        return result;
    }

    private HotkeyOption? ResolveHotkey(uint virtualKey, uint modifiers) =>
        HotkeyOptions.FirstOrDefault(option =>
            option.VirtualKey == virtualKey && option.Modifiers == modifiers);

    private MacroStore CreateStore()
    {
        var store = new MacroStore
        {
            SelectedMacroIndex = Math.Max(0, Macros.IndexOf(SelectedMacro)),
            ThemeMode = ThemeMode,
        };

        foreach (var macro in Macros)
        {
            store.Macros.Add(new StoredMacro
            {
                Id = macro.Id,
                Name = macro.Name,
                Content = macro.MacroText,
                StartVirtualKey = macro.StartHotkey.VirtualKey,
                StartModifiers = macro.StartHotkey.Modifiers,
                StopVirtualKey = macro.StopHotkey.VirtualKey,
                StopModifiers = macro.StopHotkey.Modifiers,
                StartDelay = macro.StartDelay,
            });
        }

        return store;
    }

    private void ScheduleSave()
    {
        if (_disposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _saveDebounce, cancellation);
        previous?.Cancel();
        _ = SaveAfterDelayAsync(cancellation);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
            var snapshot = CreateStore();
            await Task.Run(() => _macroStorageService.Save(snapshot), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    SetStatus("保存失败", exception.Message, StatusSeverity.Warning);
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _saveDebounce, null, cancellation);
            cancellation.Dispose();
        }
    }

    private HotkeyOption GetHotkey(string displayName) => HotkeyOptions.First(option => option.DisplayName == displayName);

    private static IReadOnlyList<HotkeyOption> CreateHotkeyOptions()
    {
        var options = new List<HotkeyOption>();
        var modifiers = new (string Prefix, uint Value)[]
        {
            (string.Empty, 0),
            ("Ctrl+", 0x0002),
            ("Alt+", 0x0001),
            ("Shift+", 0x0004),
            ("Ctrl+Alt+", 0x0003),
        };

        for (var functionKey = 6; functionKey <= 12; functionKey++)
        {
            var virtualKey = (uint)(0x75 + functionKey - 6);
            foreach (var modifier in modifiers)
            {
                options.Add(new HotkeyOption($"{modifier.Prefix}F{functionKey}", virtualKey, modifier.Value));
            }
        }

        return options;
    }

    private static int GetLogicalLength(string text) => WindowsLineEndings.GetLogicalLength(text);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var finalSnapshot = CreateStore();
        _disposed = true;
        Interlocked.Increment(ref _hotkeyConfigurationVersion);
        Stop();
        var pendingSave = Interlocked.Exchange(ref _saveDebounce, null);
        pendingSave?.Cancel();
        try
        {
            _macroStorageService.Save(finalSnapshot);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
        }

        Macros.CollectionChanged -= MacrosOnCollectionChanged;
        foreach (var macro in Macros)
        {
            UnsubscribeFromMacro(macro);
        }
        _globalHotkeyService?.Dispose();
        _keyboardInterceptionService.Dispose();
        _hotkeyConfigurationLock.Dispose();
    }
}
