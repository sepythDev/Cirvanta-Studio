using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CircuitForge;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Перехват необработанных исключений всех потоков
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };

        // 2. Перехват ошибок в фоновых асинхронных задачах (Task)
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved(); // предотвращает аварийное закрытие процесса
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("MainLoopException", ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex?.ToString()}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, message);
        }
        catch { /* защита от сбоя при записи лога */ }
    }
}