using Microsoft.Maui.Dispatching;

namespace VlakoveCedule.Maui.ViewModels;

/// <summary>Maršalování na UI vlákno – eventy Core chodí z background vláken.
/// Dispatcher se zafixuje v konstruktoru AppViewModel (ten vzniká na UI vlákně při vytváření okna).</summary>
internal static class UiMarshal
{
    private static IDispatcher? _ui;

    public static void Init(IDispatcher dispatcher) => _ui = dispatcher;

    public static void Ui(Action action)
    {
        var d = _ui ?? throw new InvalidOperationException("UiMarshal není inicializován.");
        if (!d.IsDispatchRequired) action();
        else d.Dispatch(action);
    }

    public static Task OnUiAsync(Action action)
    {
        var d = _ui ?? throw new InvalidOperationException("UiMarshal není inicializován.");
        if (!d.IsDispatchRequired) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.Dispatch(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}