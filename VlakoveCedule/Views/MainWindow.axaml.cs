using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VlakoveCedule.ViewModels;

namespace VlakoveCedule.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is INotifyPropertyChanged vm)
        {
            vm.PropertyChanged -= VmOnPropertyChanged;
            vm.PropertyChanged += VmOnPropertyChanged;
        }
        if (DataContext is not AppViewModel model) return;
        StationSelect.DataContext = model.Picker;
        DeparturesBoard.DataContext = model.Board;
        ArrivalsBoard.DataContext = model.Board;
        ApplyState();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedTab) or nameof(AppViewModel.Board) or nameof(AppViewModel.Picker))
            ApplyState();
    }

    private void ApplyState()
    {
        if (DataContext is not AppViewModel model) return;
        StationSelect.IsVisible = model.SelectedTab == 0;
        DeparturesBoard.IsVisible = model.SelectedTab == 1;
        ArrivalsBoard.IsVisible = model.SelectedTab == 2;
        SetClass(TabButton0, model.SelectedTab == 0, navy: true);
        SetClass(TabButton1, model.SelectedTab == 1, navy: false);
        SetClass(TabButton2, model.SelectedTab == 2, navy: false);
    }

    private void TabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppViewModel vm) return;
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var idx))
            vm.SelectedTab = idx;
    }

    private static void SetClass(Button b, bool selected, bool navy)
    {
        b.Classes.Set("selected", selected);
        b.Classes.Set("navybg", navy && selected);
    }
}