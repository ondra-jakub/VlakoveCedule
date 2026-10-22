using Avalonia.Threading;

namespace VlakoveCedule.ViewModels;

/// <summary>Maršalování na UI vlákno – eventy Core chodí z background vláken.</summary>
internal static class UiMarshal
{
    public static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public static Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) { action(); return Task.CompletedTask; }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
