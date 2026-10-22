using System.ComponentModel;
using Microsoft.Maui.Graphics;
using VlakoveCedule.Maui.ViewModels;

namespace VlakoveCedule.Maui;

/// <summary>Port MainWindow (Avalonia): přepínání záložek je code-behind (stejně jako na desktopu),
/// BindingContext pohledů nasazuje WireViewModel; SizeChanged hlásí šířku pro adaptivní tabulku.</summary>
public partial class MainPage : ContentPage
{
    // Stavy záložek: port stylů tab/tab.selected/tab.selected.navybg (horní lem dělá BoxView).
    private static readonly Color Navy = Color.FromArgb("#002b59");
    private static readonly Color Blue = Color.FromArgb("#00a1e0");
    private static readonly Color RowGray = Color.FromArgb("#f0f0f0");
    private static readonly Color White = Colors.White;

    private AppViewModel? _vm;

    public MainPage(AppViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        WireViewModel();

        // Adaptivní layout (skládací telefon): šířku stránky (dp) hlásíme VM; práh řeší VM.
        SizeChanged += (_, _) => vm.UpdateWindowWidth(Width);
    }

    private void WireViewModel()
    {
        if (BindingContext is INotifyPropertyChanged vm)
        {
            vm.PropertyChanged -= VmOnPropertyChanged;
            vm.PropertyChanged += VmOnPropertyChanged;
        }
        if (BindingContext is not AppViewModel model) return;
        StationSelect.BindingContext = model.Picker;
        DeparturesBoard.BindingContext = model.Board;
        ArrivalsBoard.BindingContext = model.Board;
        ApplyState();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedTab) or nameof(AppViewModel.Board) or nameof(AppViewModel.Picker))
            ApplyState();
    }

    private void ApplyState()
    {
        if (BindingContext is not AppViewModel model) return;
        StationSelect.IsVisible = model.SelectedTab == 0;
        DeparturesBoard.IsVisible = model.SelectedTab == 1;
        ArrivalsBoard.IsVisible = model.SelectedTab == 2;
        SetTab(TabButton0, TabStripe0, model.SelectedTab == 0, navy: true);
        SetTab(TabButton1, TabStripe1, model.SelectedTab == 1, navy: false);
        SetTab(TabButton2, TabStripe2, model.SelectedTab == 2, navy: false);
    }

    private void SetTab(Button b, BoxView stripe, bool selected, bool navy)
    {
        stripe.Color = selected ? Blue : White;
        b.BackgroundColor = selected ? (navy ? Navy : RowGray) : White;
        b.TextColor = selected ? Blue : Navy;
    }

    private void TabClick(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button b && int.TryParse(b.CommandParameter?.ToString(), out var idx))
            _vm.SelectedTab = idx;
    }
}