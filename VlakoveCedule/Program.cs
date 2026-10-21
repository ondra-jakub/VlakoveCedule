using Avalonia;

namespace VlakoveCedule;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Live self-test (no UI): ./VlakoveCedule --probe
        if (args.Contains("--probe"))
        {
            SelfTest.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}