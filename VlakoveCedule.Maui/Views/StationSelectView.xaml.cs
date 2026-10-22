using VlakoveCedule.Contracts;
using VlakoveCedule.Maui.ViewModels;

namespace VlakoveCedule.Maui.Views;

/// <summary>Port StationSelectView z Avalonie. Kliky na písmena/stanice obsluhuje code-behind
/// (CommandParameter) – Command na VM z DataTemplate se v MAUI váže problematicky (žádné $parent[ItemsControl]),
/// takže i x:Reference v šabloně řádku je podle plánu zakázaná.</summary>
public partial class StationSelectView : ContentView
{
    public StationSelectView()
    {
        InitializeComponent();
    }

    private StationPickerVm? Vm => BindingContext as StationPickerVm;

    private void OnSearchClicked(object? sender, EventArgs e) => Vm?.SearchCommand.Execute(null);

    /// <summary>Enter ve vyhledávacím poli → výběr prvního vyhovujícího (stejně jako na desktopu).</summary>
    private void OnSearchEnter(object? sender, EventArgs e) => OnSearchClicked(sender, e);

    private void OnLetterClicked(object? sender, EventArgs e)
    {
        if (Vm is { } vm && sender is Button b && b.CommandParameter is string letter)
            vm.LoadLetterCommand.Execute(letter);
    }

    private void OnSearchStationClicked(object? sender, EventArgs e)
    {
        if (Vm is { } vm && sender is Button b && b.CommandParameter is Station station)
            vm.PickSearchStationCommand.Execute(station);
    }

    private void OnLetterStationClicked(object? sender, EventArgs e)
    {
        if (Vm is { } vm && sender is Button b && b.CommandParameter is Station station)
            vm.PickLetterStationCommand.Execute(station);
    }
}