using System.Collections.ObjectModel;
using Microsoft.Maui.Dispatching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Base;
using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Services;

namespace VlakoveCedule.Maui.ViewModels;

public partial class AppViewModel : ObservableObject
{
    private readonly InfotabuleService _service;
    private CancellationTokenSource? _lifeCts;

    /// <summary>Práh šířky stránky (v dp): menší = kompaktní tabulka (zavřený skládací telefon), větší = plná.</summary>
    public const double WideThreshold = 600;

    [ObservableProperty]
    private int _selectedTab;           // 0 = Výběr stanic, 1 = Odjezdy, 2 = Příjezdy

    [ObservableProperty]
    private string _status = "Startuji…";

    [ObservableProperty]
    private string _clockText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyReason = "";

    /// <summary>Adaptivní layout dle šířky okna (skládací telefon); přepíná kompaktní/plnou tabulku.</summary>
    [ObservableProperty]
    private bool _isWide;

    public BoardPageVm Board { get; }
    public StationPickerVm Picker { get; }

    public ObservableCollection<string> Tabs { get; } = ["Výběr stanic", "Odjezdy", "Příjezdy"];

    private readonly IDispatcherTimer _clockTimer;

    /// <summary>Vytváří se na UI vlákně (resolucí z DI při tvorbě Mainu/okna) – odtud si vezme IDispatcher.</summary>
    public AppViewModel(InfotabuleService service)
    {
        _service = service;
        UiMarshal.Init(Dispatcher.GetForCurrentThread()
            ?? throw new InvalidOperationException("AppViewModel musí být vytvořen na UI vlákně."));

        Board = new BoardPageVm(service, EmitLog);
        Picker = new StationPickerVm(service);
        Picker.StationRequested += OnStationRequested;

        _service.Log += EmitLog;
        _service.StatusChanged += s => UiMarshal.Ui(() => Status = s);
        _service.BusyReasonChanged += s => UiMarshal.Ui(() => BusyReason = s);
        _service.BoardUpdated += OnBoardUpdated;
        _service.LettersChanged += letters => UiMarshal.Ui(() => Picker.SetLetterItems(letters));
        _service.LetterListed += listing => UiMarshal.Ui(() => Picker.OnLetterListed(listing));
        _service.PrefetchChanged += state => UiMarshal.Ui(() => Picker.ApplyPrefetchState(state));
        _service.RefreshFailed += () => UiMarshal.Ui(Board.OnRefreshFailed);

        _clockTimer = Dispatcher.GetForCurrentThread()!.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) =>
        {
            ClockText = DateTime.Now.ToString("HH:mm");
            Board.ClockText = ClockText;   // hodiny v hlavičce tabule (port desktopového $parent[Window] bindingu)
        };
        _clockTimer.Start();
        ClockText = DateTime.Now.ToString("HH:mm");
        Board.ClockText = ClockText;
    }

    public void Start()
    {
        _lifeCts = new CancellationTokenSource();
        _ = BootstrapAsync(_lifeCts.Token);
    }

    partial void OnSelectedTabChanged(int value)
    {
        EmitLog($"tab → {value}.");
        if (value == 0)
        {
            Board.Stop();
            _service.SetViewMode(ViewMode.Picker);
        }
        else
        {
            _service.SetViewMode(ViewMode.Board);
            Board.StartCountdown();
        }
    }

    /// <summary>Nová šířka stránky (dp) z MainPage.SizeChanged; mění IsWide jen při překročení prahu.</summary>
    public void UpdateWindowWidth(double width)
    {
        var wide = width >= WideThreshold;
        if (wide == IsWide) return;
        EmitLog($"layout → {(wide ? "široký" : "kompaktní")} (šířka {width:0} dp).");
        IsWide = wide;
        Board.IsWide = wide;
    }

    private void OnBoardUpdated(BoardSnapshot snapshot) => UiMarshal.Ui(() => Board.Load(snapshot));

    private async Task BootstrapAsync(CancellationToken ct)
    {
        IsBusy = true;
        BusyReason = "Připojuji se k Infotabuli…";
        try
        {
            await _service.StartAsync(ct).ConfigureAwait(true);
            SelectedTab = 1;
        }
        catch (Exception ex)
        {
            Status = "Chyba připojení: " + ex.Message;
            SelectedTab = 0;
        }
        finally
        {
            IsBusy = false;
            BusyReason = "";
        }
    }

    private async void OnStationRequested(StationRequestedEventArgs args)
    {
        if (IsBusy)
        {
            // překrývající se/předčasný výběr (kliknutí dvakrát, klik během bootstrapu)
            EmitLog($"pick „{args.StationName}“ IGNOROVÁN (IsBusy).");
            Status = "Počkejte, prosím, na dokončení předchozí operace…";
            return;
        }
        EmitLog($"pick „{args.StationName}“ (bucket {args.Bucket}, clientId '{args.ClientId}')…");
        IsBusy = true;
        BusyReason = $"Otevírám tabuli stanice {args.StationName}…";
        try
        {
            await _service
                .SelectStationAsync(new StationRequest(args.StationName, args.Bucket, args.ClientId), CancellationToken.None)
                .ConfigureAwait(true);
            SelectedTab = 1;
            Status = $"Tabule stanice {args.StationName}. Obnova každých {InfotabuleService.RefreshPeriodSeconds} s.";
            EmitLog($"pick „{args.StationName}“ OK (tab 1, auto-refresh běží).");
        }
        catch (Exception ex)
        {
            Status = "Chyba: " + ex.Message;
            EmitLog($"pick „{args.StationName}“ SELHAL: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyReason = "";
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        IsBusy = true;
        BusyReason = "Obnovuji…";
        try
        {
            await _service.ReconnectAsync(CancellationToken.None).ConfigureAwait(true);
            Status = "Připojeno.";
        }
        catch (Exception ex)
        {
            Status = "Chyba připojení: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyReason = "";
        }
    }

    internal static void EmitLog(string message)
        => DiagLog.Append("flow.log", $"[VM] {message}");
}