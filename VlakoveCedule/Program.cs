using Avalonia;
using VlakoveCedule.Core;

namespace VlakoveCedule;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // TEMP-DIAG: globální pasti na smrtící výjimky (jinak padá proces bez zápisu do flow.log)
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogFatal(e.ExceptionObject, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => LogFatal(e.Exception, false);

        // Live self-test (no UI): ./VlakoveCedule --probe
        if (args.Contains("--probe"))
        {
            SelfTest.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void LogFatal(object exceptionObject, bool terminating)
    {
        var logFile = System.IO.Path.Combine(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VlakoveCedule"),
            "crash.log");
        System.Diagnostics.Debugger.Break();
        var ex = exceptionObject as Exception;
        try
        {
            System.IO.File.AppendAllText(logFile,
                $"{DateTime.Now:HH:mm:ss.fff} terminating={terminating} {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n===\n");
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}