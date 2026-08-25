using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SimToAutoWirte.ViewModels;

namespace SimToAutoWirte.Services;

/// <summary>
/// Owns the native tray icon for both normal and silent launches.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly MainWindowViewModel _viewModel;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;
    private readonly TrayIcon _trayIcon;
    private readonly TrayIcons _icons;
    private readonly NativeMenuItem _stopItem;
    private readonly WindowIcon _stoppedIcon;
    private readonly WindowIcon _runningIcon;
    private bool _disposed;

    public TrayService(
        Application application,
        MainWindowViewModel viewModel,
        Action showWindow,
        Action exitApplication)
    {
        _application = application;
        _viewModel = viewModel;
        _showWindow = showWindow;
        _exitApplication = exitApplication;

        _stoppedIcon = CreateStatusIcon(Color.FromRgb(112, 123, 137));
        _runningIcon = CreateStatusIcon(Color.FromRgb(35, 177, 108));

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("显示输入演示台");
        showItem.Click += (_, _) => _showWindow();
        _stopItem = new NativeMenuItem("停止当前宏");
        _stopItem.Click += (_, _) => _viewModel.Stop();
        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => _exitApplication();
        menu.Items.Add(showItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = _stoppedIcon,
            ToolTipText = "输入演示台 · 已停止",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += TrayIconOnClicked;
        _icons = new TrayIcons();
        _icons.Add(_trayIcon);
    }

    public void Initialize()
    {
        if (_disposed)
        {
            return;
        }

        TrayIcon.SetIcons(_application, _icons);
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        UpdateStatus(_viewModel.IsRunning);
    }

    private void TrayIconOnClicked(object? sender, EventArgs e) => _showWindow();

    private void ViewModelOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsRunning))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateStatus(_viewModel.IsRunning);
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdateStatus(_viewModel.IsRunning));
        }
    }

    private void UpdateStatus(bool isRunning)
    {
        if (_disposed)
        {
            return;
        }

        _trayIcon.Icon = isRunning ? _runningIcon : _stoppedIcon;
        _trayIcon.ToolTipText = isRunning ? "输入演示台 · 运行中" : "输入演示台 · 已停止";
        _stopItem.IsEnabled = isRunning;
    }

    private static WindowIcon CreateStatusIcon(Color color)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var drawingContext = bitmap.CreateDrawingContext())
        {
            drawingContext.DrawEllipse(
                new SolidColorBrush(color),
                null,
                new Point(16, 16),
                13,
                13);
            drawingContext.DrawEllipse(
                new SolidColorBrush(Colors.White),
                null,
                new Point(16, 16),
                5,
                5);
        }

        return new WindowIcon(bitmap);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _trayIcon.Clicked -= TrayIconOnClicked;
        _trayIcon.Dispose();
        TrayIcon.SetIcons(_application, new TrayIcons());
        ((IDisposable)_runningIcon).Dispose();
        ((IDisposable)_stoppedIcon).Dispose();
    }
}
