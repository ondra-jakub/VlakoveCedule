using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Models;
using VlakoveCedule.Scraping;

namespace VlakoveCedule.ViewModels;

/// <summary>ViewModel otevřené tabule jedné stanice (odjezdy + příjezdy + autoobnova).</summary>
public partial class BoardPageVm : ObservableObject
{
    public const int RefreshPeriodSeconds = 60;

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _countdownTimer;
    private int _secondsLeft = RefreshPeriodSeconds;

    public ObservableCollection<BoardRowVm> Departures { get; } = [];
    public ObservableCollection<BoardRowVm> Arrivals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    [NotifyPropertyChangedFor(nameof(NoData))]
    private string _stationName = "— vyberte stanici —";

    [ObservableProperty]
    private string? _stationInfoUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoData))]
    private bool _hasData;

    [ObservableProperty]
    private string _lastUpdated = "";

    [ObservableProperty]
    private int _secondsUntilRefresh;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _refreshHint = "";

    /// <summary>Zpráva pro prázdnou tabuli.</summary>
    public bool NoData => HasData == false && StationName != null && !StationName.StartsWith("—");

    private readonly Func<CancellationToken, Task<(IReadOnlyList<BoardRow> dep, IReadOnlyList<BoardRow> arr)>>? _refresh;
    private readonly Action<string>? _log;

    private BoardPageVm()
    {
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(RefreshPeriodSeconds),
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            Log("ui-canary: refresh tick fired.");
            await RefreshNowAsync().ConfigureAwait(true);
        };
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdownTimer.Tick += (_, _) =>
        {
            // TEMP-DIAG: canary – stopovačka, že 1s tick pumpuje i na této úrovni
            if (DateTime.Now.Second % 15 == 0) Log("ui-canary: board countdown tick.");
            if (_refreshTimer.IsEnabled && _secondsLeft > 0)
                SecondsUntilRefresh = --_secondsLeft;
            else
                SecondsUntilRefresh = 0;
            RefreshHint = _refreshTimer.IsEnabled ? $"Obnova za {SecondsUntilRefresh} s" : "";
        };
    }

    public BoardPageVm(Func<CancellationToken, Task<(IReadOnlyList<BoardRow>, IReadOnlyList<BoardRow>)>> refresh,
                        Action<string>? log = null) : this()
    {
        _refresh = refresh;
        _log = log;
        _countdownTimer.Start();
    }

    private void Log(string message) => _log?.Invoke(message);

    public void StartAutoRefresh()
    {
        Log($"StartAutoRefresh (interval {RefreshPeriodSeconds}s, fáze se zarovnává).");
        _secondsLeft = RefreshPeriodSeconds;
        SecondsUntilRefresh = _secondsLeft;
        RefreshHint = $"Obnova za {_secondsLeft} s";
        // DŮLEŽITÉ: u běžícího DispatcherTimeru nemůže Start() v Avalonii přesunout fázi
        // (obnova by přicházela podle starého fáze a odpočet by s ní nesouhlasil) → Stop a znovu Start.
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void Stop()
    {
        Log($"Stop (refreshTimer IsEnabled={_refreshTimer.IsEnabled}).");
        _refreshTimer.Stop();
        _countdownTimer.Stop();
        RefreshHint = "";
    }

    /// <summary>Nacte novou tabuli (klid i opakování).</summary>
    public void Load(SzdcParser.BoardInfo board)
    {
        Log($"Load: „{board.StationName}“ ({board.Departures.Count}/{board.Arrivals.Count}); _secondsLeft={_secondsLeft}.");
        StationName = board.StationName;
        StationInfoUrl = board.StationInfoUrl;
        SetRows(Departures, board.Departures);
        SetRows(Arrivals, board.Arrivals);
        HasData = Departures.Count > 0 || Arrivals.Count > 0;
        LastUpdated = "Naposledy: " + DateTime.Now.ToString("HH:mm:ss");
    }

    [RelayCommand]
    private async Task RefreshNowAsync()
    {
        if (_refresh is null) { Log("RefreshNow: skip (bez refresh callbacku)."); return; }
        if (IsRefreshing) { Log("RefreshNow: skip (u probíhá)."); return; }
        Log("RefreshNow: start…");
        IsRefreshing = true;
        try
        {
            var (dep, arr) = await _refresh(CancellationToken.None).ConfigureAwait(true);
            SetRows(Departures, dep);
            SetRows(Arrivals, arr);
            HasData = Departures.Count > 0 || Arrivals.Count > 0;
            LastUpdated = "Naposledy: " + DateTime.Now.ToString("HH:mm:ss");
            _secondsLeft = RefreshPeriodSeconds;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("refresh error: " + ex.Message);
            _secondsLeft = RefreshPeriodSeconds;      // chybná obnova nesmí zamrznout odpočítávání
            Log("RefreshNow: CHYBA " + ex.GetType().Name + ": " + ex.Message);
            RefreshHint = "Obnova se nezdařila";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void OpenStationInfo()
    {
        if (StationInfoUrl is { Length: > 0 })
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = StationInfoUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("open info: " + ex.Message);
            }
        }
    }

    private static void SetRows(ObservableCollection<BoardRowVm> target, IReadOnlyList<BoardRow> rows)
    {
        target.Clear();
        var odd = false;
        foreach (var r in rows)
        {
            target.Add(BoardRowVm.From(r, odd));
            odd = !odd;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}