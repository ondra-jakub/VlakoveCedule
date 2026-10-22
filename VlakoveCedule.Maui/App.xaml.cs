using Microsoft.Extensions.DependencyInjection;
using VlakoveCedule.Core.Services;
using VlakoveCedule.Maui.ViewModels;

namespace VlakoveCedule.Maui;

public partial class App : Application
{
	private AppViewModel? _vm;

	public App()
	{
		InitializeComponent();
	}

	/// <summary>Ekvivalent OnFrameworkInitializationCompleted v Avalonii: z DI se vytvoří VM a hlavní
	/// stránka (na UI vlákně); Start() až v OnStart – totožné pořadí jako na desktopu.</summary>
	protected override Window CreateWindow(IActivationState? activationState)
	{
		_vm = Services().GetRequiredService<AppViewModel>();
		return new Window(Services().GetRequiredService<MainPage>());
	}

	protected override void OnStart()
	{
		base.OnStart();
		(_vm ?? Services().GetService<AppViewModel>())?.Start();
	}

	protected override void OnResume()
	{
		base.OnResume();
		try
		{
			// Po návratu z pozadí (Android 12+ zamrzne proces → zastaralé ticky): best-effort obnova.
			var service = Services().GetService<InfotabuleService>();
			if (service is { IsViewBoard: true })
				_ = service.RefreshNowAsync();
		}
		catch
		{
			// best effort – obnovu vystřídá normální cyklus
		}
	}

	private static IServiceProvider Services() =>
		IPlatformApplication.Current?.Services
			?? throw new InvalidOperationException("Maui služby nejsou inicializovány.");
}