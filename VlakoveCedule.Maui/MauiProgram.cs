using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VlakoveCedule.Base;
using VlakoveCedule.Core.Services;
using VlakoveCedule.Maui.ViewModels;

namespace VlakoveCedule.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Adresář app dat se musí zafixovat před jakýmikoli logy/stavem (DiagLog, AppStateStore).
		AppPaths.Configure(Path.Combine(FileSystem.AppDataDirectory, "VlakoveCedule"));

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<InfotabuleService>();
		builder.Services.AddSingleton<AppViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}