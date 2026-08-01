using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HighlightForge.Core.Diagnostics;

namespace HighlightForge.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception) HighlightForgeLog.ErrorAsync("Unhandled application exception.", exception).GetAwaiter().GetResult();
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            HighlightForgeLog.ErrorAsync("Unobserved task exception.", eventArgs.Exception).GetAwaiter().GetResult();
            eventArgs.SetObserved();
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
