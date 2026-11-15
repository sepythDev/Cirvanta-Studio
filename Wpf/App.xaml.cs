using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CircuitForge;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CircuitForge");
    private static readonly string LogPath = Path.Combine(LogDirectory, "crash.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteException("WPF Dispatcher", e.Exception);
        try
        {
            MessageBox.Show(
                $"CircuitForge caught an unexpected .NET error. Details were written to:\n{LogPath}\n\n{e.Exception.GetType().FullName}: {e.Exception.Message}",
                "CircuitForge - error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        // Leave the exception unhandled so the app does not continue in an unknown state.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteException("AppDomain unhandled exception", exception);
        else
            WriteException("AppDomain unhandled exception", new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception"));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException("Unobserved task exception", e.Exception);
    }
}
