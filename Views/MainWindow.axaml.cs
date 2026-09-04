using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SimToAutoWirte.ViewModels;

namespace SimToAutoWirte.Views;

public partial class MainWindow : Window
{
    private const double EditorLineHeight = 20;

    /// <summary>补偿未对齐到整行的滚动偏移。</summary>
    private readonly TranslateTransform _gutterTransform = new();

    private ScrollViewer? _editorScrollViewer;
    private int _lineCount = 1;
    private bool _isOpen;

    public MainWindow()
    {
        InitializeComponent();

        LineNumbers.RenderTransform = _gutterTransform;
        Editor.PropertyChanged += Editor_OnPropertyChanged;
        Editor.TemplateApplied += Editor_OnTemplateApplied;
        Editor.LayoutUpdated += Editor_OnLayoutUpdated;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnOpened(object? sender, EventArgs e)
    {
        _isOpen = true;
        UpdateLineNumbers();
        UpdateCaretPosition();
        AttachEditorScrollViewer();

        if (ViewModel is { } viewModel)
        {
            await viewModel.ConfigureHotkeysAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isOpen = false;
        Editor.PropertyChanged -= Editor_OnPropertyChanged;
        Editor.TemplateApplied -= Editor_OnTemplateApplied;
        Editor.LayoutUpdated -= Editor_OnLayoutUpdated;

        if (_editorScrollViewer is { } scrollViewer)
        {
            scrollViewer.ScrollChanged -= EditorScrollViewer_OnScrollChanged;
            _editorScrollViewer = null;
        }

        ViewModel?.Dispose();
    }

    // ─────────────  编辑器行号与光标位置  ─────────────

    private void Editor_OnTemplateApplied(object? sender, TemplateAppliedEventArgs e) => AttachEditorScrollViewer();

    // TextBox 模板应用完成时，内部 ScrollViewer 在部分平台还没有挂入视觉树。
    // LayoutUpdated 是一次可靠的补偿机会，避免行号永远停在首屏可视的十几行。
    private void Editor_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_editorScrollViewer is null)
        {
            AttachEditorScrollViewer();
        }
        else
        {
            Editor.LayoutUpdated -= Editor_OnLayoutUpdated;
        }
    }

    /// <summary>
    /// 行号槽固定在左侧不参与横向滚动，纵向偏移镜像编辑器自身的 ScrollViewer。
    /// 这样编辑器保留原生滚动（光标跟随、键盘导航都正常），行号也始终可见。
    /// </summary>
    private void AttachEditorScrollViewer()
    {
        if (_editorScrollViewer is not null)
        {
            return;
        }

        // 注意：Opened 触发时 TextBox 的模板尚未展开（此时只有 1 个视觉子元素），
        // 必须依赖 TemplateApplied 这一路才能拿到内部 ScrollViewer。两处都调用是有意的。
        var scrollViewer = Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null)
        {
            return;
        }

        _editorScrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += EditorScrollViewer_OnScrollChanged;
        SyncGutterOffset();
        Editor.LayoutUpdated -= Editor_OnLayoutUpdated;
    }

    private void EditorScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e) => SyncGutterOffset();

    private void SyncGutterOffset()
    {
        var offset = _editorScrollViewer?.Offset.Y ?? 0;
        var viewportHeight = _editorScrollViewer?.Viewport.Height ?? Bounds.Height;
        var firstVisibleLineIndex = Math.Max(0, (int)Math.Floor(offset / EditorLineHeight));
        var visibleLineCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / EditorLineHeight) + 1);
        var lastVisibleLine = Math.Min(_lineCount, firstVisibleLineIndex + visibleLineCount);

        var builder = new StringBuilder(Math.Max(1, lastVisibleLine - firstVisibleLineIndex) * 4);
        for (var line = firstVisibleLineIndex + 1; line <= lastVisibleLine; line++)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        LineNumbers.Text = builder.ToString();
        LineNumbers.MinWidth = Math.Max(22, _lineCount.ToString().Length * 8);
        _gutterTransform.Y = -(offset % EditorLineHeight);
    }

    private void Editor_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            UpdateLineNumbers();
            UpdateCaretPosition();
        }
        else if (e.Property == TextBox.CaretIndexProperty)
        {
            UpdateCaretPosition();
        }
    }

    private void UpdateLineNumbers()
    {
        var text = Editor.Text ?? string.Empty;
        var lineCount = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lineCount++;
            }
        }

        _lineCount = lineCount;
        SyncGutterOffset();
    }

    private void UpdateCaretPosition()
    {
        var text = Editor.Text ?? string.Empty;
        var caret = Math.Clamp(Editor.CaretIndex, 0, text.Length);

        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < caret; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        CaretPosition.Text = $"行 {line}，列 {caret - lineStart + 1}";
    }

    // ─────────────  主题  ─────────────

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleThemeMode();

    // ─────────────  运行控制  ─────────────

    private async void StartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        WindowState = WindowState.Minimized;
        await ViewModel.StartAsync(useCountdown: true);
    }

    private void StopButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.PauseOrStop();

    // ─────────────  宏组管理  ─────────────

    private async void AddMacroButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddMacro();
        if (ViewModel is not null)
        {
            await ViewModel.ConfigureHotkeysAsync();
        }
    }

    private async void DuplicateMacroButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.DuplicateSelectedMacro();
        if (ViewModel is not null)
        {
            await ViewModel.ConfigureHotkeysAsync();
        }
    }

    private async void RemoveMacroButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RemoveSelectedMacro();
        if (ViewModel is not null)
        {
            await ViewModel.ConfigureHotkeysAsync();
        }
    }

    // ─────────────  内容导入导出  ─────────────

    private void LoadSampleButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.LoadSample();

    private void ClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.SelectedMacro.MacroText = string.Empty;
        }
    }

    private async void ImportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入宏内容",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("代码和文本")
                {
                    Patterns = ["*.txt", "*.cs", "*.json", "*.js", "*.ts", "*.py", "*.java", "*.cpp", "*.h", "*.html", "*.css", "*.sql", "*.md"],
                },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        ViewModel.SelectedMacro.MacroText = await reader.ReadToEndAsync();
    }

    private async void ExportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出宏内容",
            SuggestedFileName = "keyboard-macro.txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("文本文件") { Patterns = ["*.txt"] },
                FilePickerFileTypes.All,
            ],
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(ViewModel.SelectedMacro.MacroText);
    }

    private async void Hotkey_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isOpen && ViewModel is not null)
        {
            await ViewModel.ConfigureHotkeysAsync();
        }
    }
}
