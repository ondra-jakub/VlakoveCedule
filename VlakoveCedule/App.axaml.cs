using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var vm = new AppViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            vm.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}