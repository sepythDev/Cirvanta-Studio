using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CircuitForge;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cirvanta");
private static readonly string LogPath = Path.Combine(LogDirectory, "crash.log");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => WriteException("Unobserved task exception", e.Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => WriteException("Avalonia UI thread", e.Exception);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    private static void WriteException(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = new StringBuilder()
                .AppendLine("============================================================")
                .AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}")
                .AppendLine($"Source: {source}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine(exception.ToString())
                .ToString();
            File.AppendAllText(LogPath, entry);
        }
        catch
        {
            // Logging must never cause a second crash.
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteException("AppDomain unhandled exception", exception);
        else
            WriteException("AppDomain unhandled exception", new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception"));
    }
}
