using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VlakoveCedule.Core.Services;
using VlakoveCedule.ViewModels;
using VlakoveCedule.Views;

namespace VlakoveCedule;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var service = new InfotabuleService();
            var vm = new AppViewModel(service);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            vm.Start();
            if (Environment.GetCommandLineArgs().Contains("--autotest"))
                AppViewModel.StartAutoTest(vm);   // TEMP-DIAG: automatická reprodukce flow bez klikání
        }

        base.OnFrameworkInitializationCompleted();
    }
}