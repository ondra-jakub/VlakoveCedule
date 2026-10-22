using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Services;

namespace VlakoveCedule.Maui.ViewModels;

/// <summary>ViewModel otevřené tabule jedné stanice (odjezdy + příjezdy + vizuální odpočet obnovy).
/// Časování obnovy řeší <see cref="InfotabuleService"/>; zde je jen 1s odpočet čtený z NextRefreshAt.
/// Port z Avalonie: timer nad <c>IDispatcher</c>, OpenStationInfo přes <see cref="Launcher"/>; navíc
/// ClockText + IsWide – hodiny a přepnutí kompaktní/plná tabulka přeposílá AppViewModel,
/// protože x:Reference na kořenovou stránku se v MAUI v samostatném view vyhodnotit nedá.</summary>
public partial class BoardPageVm : ObservableObject
{
    private readonly InfotabuleService _service;
    private readonly IDispatcherTimer _countdownTimer;
    private readonly Action<string>? _log;

    public ObservableCollection<BoardRowVm> Departures { get; } = [];
    public ObservableCollection<BoardRowVm> Arrivals { get; } = [];

    /// <summary>Hodiny z AppViewModel (viz poznámka nahoře).</summary>
    [ObservableProperty]
    private string _clockText = "";

    /// <summary>Široký layout (otevřený skládací telefon) – přepíše AppViewModel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    private bool _isWide;

    /// <summary>Kompaktní tabulka pro zavřený skládací telefon.</summary>
    public bool IsCompact => !IsWide;

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
    private string _refreshHint = "";

    /// <summary>Zpráva pro prázdnou tabuli.</summary>
    public bool NoData => HasData == false && StationName != null && !StationName.StartsWith("—");

    public BoardPageVm(InfotabuleService service, Action<string>? log = null)
    {
        _service = service;
        _log = log;
        _countdownTimer = Dispatcher.GetForCurrentThread()!.CreateTimer();
        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
    }

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>Spustí vizuální odpočet (fázi obnovovacího cyklu řídí služba přes NextRefreshAt).</summary>
    public void StartCountdown()
    {
        UpdateCountdown();
        _countdownTimer.Start();
    }

    public void Stop()
    {
        _countdownTimer.Stop();
        SecondsUntilRefresh = 0;
        RefreshHint = "";
    }

    private void UpdateCountdown()
    {
        var next = _service.NextRefreshAt;
        if (next is null)
        {
            SecondsUntilRefresh = 0;
            RefreshHint = "";
            return;
        }
        var seconds = (int)Math.Ceiling((next.Value - DateTimeOffset.Now).TotalSeconds);
        if (seconds < 0) seconds = 0;
        SecondsUntilRefresh = seconds;
        RefreshHint = $"Obnova za {seconds} s";
    }

    /// <summary>Načte novou tabuli (klid i opakování).</summary>
    public void Load(BoardSnapshot board)
    {
        Log($"Load: „{board.StationName}“ ({board.Departures.Count}/{board.Arrivals.Count}).");
        StationName = board.StationName;
        StationInfoUrl = board.StationInfoUrl;
        SetRows(Departures, board.Departures);
        SetRows(Arrivals, board.Arrivals);
        HasData = Departures.Count > 0 || Arrivals.Count > 0;
        LastUpdated = "Naposledy: " + board.LastUpdated.LocalDateTime.ToString("HH:mm:ss");
    }

    /// <summary>Chybná obnova – odpočet se resetuje ve službě, zde jen upozornění (přepíše ho další tick).</summary>
    public void OnRefreshFailed()
    {
        RefreshHint = "Obnova se nezdařila";
    }

    [RelayCommand]
    private async Task OpenStationInfo()
    {
        if (StationInfoUrl is { Length: > 0 })
        {
            try
            {
                await Launcher.OpenAsync(new Uri(StationInfoUrl));
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