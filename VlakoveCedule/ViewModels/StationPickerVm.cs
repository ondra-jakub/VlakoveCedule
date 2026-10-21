using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Models;
using VlakoveCedule.Scraping;

namespace VlakoveCedule.ViewModels;

/// <summary>Požadavek na otevření stanice. Otevírá se vždy podle názvu + bucketu (atomický tok);
/// ClientId se nepoužívá (vždy „“), neboť je platné jen do další změny serverového view.</summary>
public sealed record StationRequestedEventArgs(string StationName, string ClientId, string Bucket);

/// <summary>ViewModel záložky „Výběr stanic“: vyhledávač + abeceda + seznam.</summary>
public partial class StationPickerVm : ObservableObject
{
    private readonly Func<IReadOnlyList<LetterItem>> _letters;
    private readonly Func<string, CancellationToken, Task<LetterListing>> _loadLetter;
    private readonly Func<CancellationToken, Task> _prefetchAll;

    private readonly object _bucketsLock = new();
    private Dictionary<string, string> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _prefetchCts;

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

    public StationPickerVm(
        Func<IReadOnlyList<LetterItem>> letters,
        Func<string, CancellationToken, Task<LetterListing>> loadLetter,
        Func<CancellationToken, Task> prefetchAll)
    {
        _letters = letters;
        _loadLetter = loadLetter;
        _prefetchAll = prefetchAll;
        RefreshLetterItems();
    }

    public void RefreshLetterItems()
    {
        Ui(() => LetterItems = new ObservableCollection<LetterItem>(_letters()));
    }

    private static void Ui(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    /// <summary>Načtení kompletního seznamu stanic na pozadí (pro vyhledávač).</summary>
    public void StartPrefetch()
    {
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var cts = _prefetchCts;
        _ = Task.Run(() => PrefetchLoopAsync(cts!.Token));
    }

    /// <summary>Pozastavit prefetch (během výběru stanice / zobrazování tabule) – jinak měnil serverové view a rozbíjal klik na stanici.</summary>
    public volatile bool PrefetchPaused;

    private async Task PrefetchLoopAsync(CancellationToken ct)
    {
        Ui(() => IsPrefetching = true);
        try
        {
            var letters = _letters().Select(l => l.Letter).ToList();
            for (var i = 0; i < letters.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (PrefetchPaused)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                Ui(() => LoadProgress = $"Načítám kompletní seznam stanic… ({i + 1}/{letters.Count})");
                try
                {
                    var lst = await _loadLetter(letters[i], ct).ConfigureAwait(false);
                    lock (_bucketsLock)
                        foreach (var s in lst.Stations)
                            _buckets[s.Name] = lst.Bucket;
                    if (ActiveLetter == letters[i])
                        Ui(() => ReloadLetterStations(lst, letters[i]));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // jednotlivé písmeno nevadí
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            var total = 0;
            lock (_bucketsLock) total = _buckets.Count;
            Ui(() => LoadProgress = $"Seznam stanic je kompletní ({total} stanic).");
        }
        catch (OperationCanceledException)
        {
            SetProgressQuiet("");
        }
        finally
        {
            Ui(() => IsPrefetching = false);
        }
        Ui(RefreshSearch);
    }

    private void SetProgressQuiet(string text) => Ui(() => LoadProgress = text);

    public string? BucketFor(string name)
    {
        lock (_bucketsLock)
            return _buckets.TryGetValue(name, out var b) ? b : null;
    }

    [RelayCommand]
    private async Task LoadLetter(string letter)
    {
        ActiveLetter = letter;
        try
        {
            var lst = await _loadLetter(letter, CancellationToken.None).ConfigureAwait(false);
            await OnUiAsync(() => ReloadLetterStations(lst, letter)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Heading = $"Seznam „{letter}“ se nepodařilo načíst ({ex.GetType().Name})";
        }
    }

    private static Task OnUiAsync(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { action(); return Task.CompletedTask; }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private void ReloadLetterStations(LetterListing lst, string letter)
    {
        LetterStations.Clear();
        foreach (var s in lst.Stations)
            LetterStations.Add(s);
        Heading = $"Stanice na písmeno „{letter}“ ({lst.Stations.Count})";
    }

    [RelayCommand]
    private Task Search() => SearchResults.Count > 0
        ? RequestAsync(new StationRequestedEventArgs(SearchResults[0].Name, "", BucketFor(SearchResults[0].Name) ?? SzdcClient.GuessBucket(SearchResults[0].Name)))
        : Task.CompletedTask;

    /// <summary>Enter ve vyhledávacím poli → výběr prvního vyhovujícího.</summary>
    [RelayCommand]
    private Task SearchEnter() => SearchResults.Count > 0
        ? RequestAsync(new StationRequestedEventArgs(SearchResults[0].Name, "", BucketFor(SearchResults[0].Name) ?? SzdcClient.GuessBucket(SearchResults[0].Name)))
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
    private static StationRequestedEventArgs ToArgs(Station station) =>
        new(station.Name, "", !string.IsNullOrEmpty(station.Bucket)
            ? station.Bucket
            : SzdcClient.GuessBucket(station.Name));

    private async Task RequestAsync(StationRequestedEventArgs args)
    {
        await OnUiAsync(() => StationRequested?.Invoke(args)).ConfigureAwait(false);
    }

    partial void OnSearchTextChanged(string value) => Ui(RefreshSearch);

    partial void OnLoadProgressChanged(string value) => Ui(() => OnPropertyChanged(nameof(LoadProgressVisible)));

    partial void OnActiveLetterChanged(string? value) => Ui(() => OnPropertyChanged(nameof(LetterActive)));

    private void RefreshSearch()
    {
        var q = SearchText.Trim();
        if (q.Length == 0)
        {
            SearchResults.Clear();
            Heading = "Vyberte stanici";
            return;
        }
        List<(string name, string bucket)> matches;
        lock (_bucketsLock)
            matches = _buckets
                .Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.CurrentCulture)
                .Take(200)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        SearchResults.Clear();
        foreach (var (name, bucket) in matches)
            SearchResults.Add(new Station { Name = name, ClientId = "", Bucket = bucket });
        int total;
        lock (_bucketsLock) total = _buckets.Keys.Count(k => k.Contains(q, StringComparison.OrdinalIgnoreCase));
        Heading = $"Výsledky pro „{q}“ (celkem {total})";
    }
}