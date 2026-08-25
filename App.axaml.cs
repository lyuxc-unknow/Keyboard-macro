using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SimToAutoWirte.ViewModels;
using SimToAutoWirte.Views;
using SimToAutoWirte.Services;

namespace SimToAutoWirte
{
    public partial class App : Application
    {
        private MainWindowViewModel? _backgroundViewModel;
        private MainWindowViewModel? _viewModel;
        private MainWindow? _mainWindow;
        private TrayService? _trayService;
        private bool _allowWindowClose;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _viewModel = new MainWindowViewModel();
                _trayService = new TrayService(this, _viewModel, ShowMainWindow, ExitApplication);
                _trayService.Initialize();
                desktop.Exit += (_, _) => DisposeApplication();

                if (Program.IsSilentStartup(desktop.Args))
                {
                    _backgroundViewModel = _viewModel;
                    Dispatcher.UIThread.Post(InitializeBackgroundServices);
                }
                else
                {
                    EnsureMainWindow();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private async void InitializeBackgroundServices()
        {
            if (_backgroundViewModel is null)
            {
                return;
            }

            try
            {
                await _backgroundViewModel.ConfigureHotkeysAsync();
            }
            catch
            {
                DisposeBackgroundViewModel();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown(1);
                }
            }
        }

        private void DisposeBackgroundViewModel()
        {
            _backgroundViewModel?.Dispose();
            _backgroundViewModel = null;
        }

        private void DisposeApplication()
        {
            _trayService?.Dispose();
            _trayService = null;
            _viewModel?.Dispose();
            _viewModel = null;
            _backgroundViewModel = null;
        }

        private void ShowMainWindow()
        {
            if (_viewModel is null || _allowWindowClose)
            {
                return;
            }

            EnsureMainWindow();
            if (_mainWindow is null)
            {
                return;
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private void EnsureMainWindow()
        {
            if (_viewModel is null || _mainWindow is not null)
            {
                return;
            }

            _mainWindow = new MainWindow { DataContext = _viewModel };
            _mainWindow.Closing += MainWindowOnClosing;
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = _mainWindow;
            }
        }

        private void MainWindowOnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_allowWindowClose)
            {
                return;
            }

            e.Cancel = true;
            _mainWindow?.Hide();
        }

        private void ExitApplication()
        {
            _allowWindowClose = true;
            _viewModel?.Stop();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }
}
