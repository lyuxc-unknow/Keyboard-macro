using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Linq;
using Avalonia.Controls;

namespace SimToAutoWirte
{
    internal sealed class Program
    {
        internal const string SilentArgument = "--silent";

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        internal static bool IsSilentStartup(string[]? args) =>
            args?.Any(argument => string.Equals(argument, SilentArgument, StringComparison.OrdinalIgnoreCase)) == true;

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI();
    }
}
