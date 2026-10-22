using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Services;

namespace VlakoveCedule.Maui.ViewModels;

/// <summary>Požadavek na otevření stanice. Otevírá se vždy podle názvu + bucketu (atomický tok);
/// ClientId se nepoužívá (vždy „“), neboť je platné jen do další změny serverového view.</summary>
public sealed record StationRequestedEventArgs(string StationName, string ClientId, string Bucket);

/// <summary>ViewModel záložky „Výběr stanic“: vyhledávač + abeceda + seznam.
/// Data (písmena, listing, prefetch, katalog) dodává <see cref="InfotabuleService"/> přes eventy.</summary>
public partial class StationPickerVm : ObservableObject
{
    private readonly InfotabuleService _service;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _heading = "Vyberte stanici";

    [ObservableProperty]
    private bool _isPrefetching;

    [ObservableProperty]
    private string _loadProgress = "";

    [ObservableProperty]
    private string? _activeLetter;

    [ObservableProperty]
    private ObservableCollection<LetterItem> _letterItems = [];

    /// <summary>Zobrazovat progress načítání (vazba IsVisible).</summary>
    public bool LoadProgressVisible => !string.IsNullOrEmpty(LoadProgress);

    /// <summary>Aktivní vyhledávání (vazba IsVisible).</summary>
    public bool SearchActive => SearchText.Trim().Length > 0;

    /// <summary>Zobrazený seznam písmena (vazba IsVisible).</summary>
    public bool LetterActive => ActiveLetter != null && SearchText.Trim().Length == 0;

    public ObservableCollection<Station> LetterStations { get; } = [];
    public ObservableCollection<Station> SearchResults { get; } = [];

    /// <summary>Vybraná stanice – obsluhuje AppViewModel.</summary>
    public event Action<StationRequestedEventArgs>? StationRequested;

    public StationPickerVm(InfotabuleService service)
    {
        _service = service;
    }

    /// <summary>Písmena z události služby (volá se na UI vlákně).</summary>
    public void SetLetterItems(IReadOnlyList<LetterItem> letters)
        => LetterItems = new ObservableCollection<LetterItem>(letters);

    /// <summary>Nový listing písmena z události služby (volá se na UI vlákně).</summary>
    public void OnLetterListed(LetterListing listing) => ReloadLetterStations(listing, listing.Bucket);

    /// <summary>Stav prefetch smyčky z události služby (volá se na UI vlákně).</summary>
    public void ApplyPrefetchState(PrefetchState state)
    {
        IsPrefetching = state.IsPrefetching;
        LoadProgress = state.ProgressText;
        if (!state.IsPrefetching) RefreshSearch();
    }

    [RelayCommand]
    private async Task LoadLetter(string letter)
    {
        ActiveLetter = letter;
        try
        {
            await _service.ListLetterAsync(letter, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Heading = $"Seznam „{letter}“ se nepodařilo načíst ({ex.GetType().Name})";
        }
    }

    private void ReloadLetterStations(LetterListing listing, string letter)
    {
        LetterStations.Clear();
        foreach (var s in listing.Stations)
            LetterStations.Add(s);
        Heading = $"Stanice na písmeno „{letter}“ ({listing.Stations.Count})";
    }

    [RelayCommand]
    private Task Search() => SearchResults.Count > 0
        ? RequestAsync(new StationRequestedEventArgs(SearchResults[0].Name, "", _service.BucketFor(SearchResults[0].Name)))
        : Task.CompletedTask;

    [RelayCommand]
    private Task PickLetterStation(Station station) => RequestAsync(ToArgs(station));
    [RelayCommand]
    private Task PickSearchStation(Station station) => RequestAsync(ToArgs(station));

    /// <summary>
    /// Výběr stanice vždy přes název + bucket (atomický tok „klik na písmeno → čerstvý list → klik na
    /// tlačítko stanice“), stejně jako ve vyhledávání. ClientId z listingu je platné jen do další
    /// změny serverového view – tu přepíše prefetch i každé předchozí otevření stanice. Klik na staré
    /// id buď selže, nebo server re-renderuje aktuální view (starou tabuli) a parser ho vyhodnotí
    /// jako úspěch – proto fungoval jen první klik na stanici pod písmenem a další vyžadovaly
    /// nový klik na písmeno (čerstvý list).
    /// </summary>
    private StationRequestedEventArgs ToArgs(Station station) =>
        new(station.Name, "", !string.IsNullOrEmpty(station.Bucket) ? station.Bucket : _service.BucketFor(station.Name));

    private async Task RequestAsync(StationRequestedEventArgs args)
    {
        await UiMarshal.OnUiAsync(() => StationRequested?.Invoke(args)).ConfigureAwait(false);
    }

    partial void OnSearchTextChanged(string value) => UiMarshal.Ui(RefreshSearch);

    partial void OnLoadProgressChanged(string value) => UiMarshal.Ui(() => OnPropertyChanged(nameof(LoadProgressVisible)));

    partial void OnActiveLetterChanged(string? value) => UiMarshal.Ui(() => OnPropertyChanged(nameof(LetterActive)));

    private void RefreshSearch()
    {
        var q = SearchText.Trim();
        if (q.Length == 0)
        {
            SearchResults.Clear();
            Heading = "Vyberte stanici";
            return;
        }
        var result = _service.Search(q);
        SearchResults.Clear();
        foreach (var s in result.Stations)
            SearchResults.Add(s);
        Heading = $"Výsledky pro „{q}“ (celkem {result.TotalCount})";
    }
}