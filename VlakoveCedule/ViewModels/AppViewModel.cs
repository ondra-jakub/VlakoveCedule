using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Base;
using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Services;

namespace VlakoveCedule.ViewModels;

public partial class AppViewModel : ObservableObject
{
    private readonly InfotabuleService _service;
    private CancellationTokenSource? _lifeCts;

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

    public BoardPageVm Board { get; }
    public StationPickerVm Picker { get; }

    public ObservableCollection<string> Tabs { get; } = ["Výběr stanic", "Odjezdy", "Příjezdy"];

    private readonly DispatcherTimer _clockTimer;

    public AppViewModel(InfotabuleService service)
    {
        _service = service;
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

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm");
        _clockTimer.Start();
        ClockText = DateTime.Now.ToString("HH:mm");
    }

    public void Start()
    {
        _lifeCts = new CancellationTokenSource();
        // TEMP-DIAG: srdeční tep z pozadí (dokáže, že proces žije, když přestanou UI ticky)
        _ = Task.Run(() => HeartbeatAsync(_lifeCts.Token));
        _ = BootstrapAsync(_lifeCts.Token);
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10000, ct).ConfigureAwait(false);
            EmitLog($"hb +{sw.Elapsed.TotalSeconds:0}s");
        }
    }

    /// <summary>TEMP-DIAG: automatický driver – replikuje uživatelský flow přímými RelayCommandy:
    /// tab 0 → klik na písmeno → klik na stanici z listingu → (skok na tab 1) → čekání na 60-s obnovy.</summary>
    internal static void StartAutoTest(AppViewModel vm) => _ = Task.Run(() => StartAutoTestAsync(vm, CancellationToken.None));

    private static async Task StartAutoTestAsync(AppViewModel vm, CancellationToken ct)
    {
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        EmitLog("AUTOTEST: start.");
        try
        {
            // čekání na bootstrap
            var bootDone = false;
            for (var i = 0; i < 240; i++)
            {
                if (!await ui.InvokeAsync(() => vm.IsBusy).GetTask()) { bootDone = true; break; }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            var tabAfterBoot = await ui.InvokeAsync(() => vm.SelectedTab).GetTask();
            EmitLog($"AUTOTEST: bootstrap hotov ({bootDone}, tab {tabAfterBoot}).");
            await Task.Delay(2000, ct).ConfigureAwait(false);

            var letters = new[] { "C", "M", "Ch", "P" };
            for (var cycle = 0; cycle < letters.Length; cycle++)
            {
                await ui.InvokeAsync(() => vm.SelectedTab = 0);
                EmitLog($"AUTOTEST: cyklus {cycle + 1}: tab 0…");
                await Task.Delay(3000, ct).ConfigureAwait(false);   // prefetch stihne mezitím měnit view (jako u reálného uživatele)

                await ui.InvokeAsync(() => vm.Picker.LoadLetterCommand.Execute(letters[cycle]));
                await Task.Delay(2500, ct).ConfigureAwait(false);   // listing načten

                var station = await ui.InvokeAsync(() =>
                    vm.Picker.LetterStations.ElementAtOrDefault(cycle % 3)).GetTask();
                if (station is null) { EmitLog("AUTOTEST: listing prázdný?!"); continue; }
                EmitLog($"AUTOTEST: cyklus {cycle + 1}: pick „{station.Name}“ (bucket {station.Bucket})…");
                await ui.InvokeAsync(() => vm.Picker.PickLetterStationCommand.Execute(station));
                await Task.Delay(4000, ct).ConfigureAwait(false);   // pick dokončen → skok na tab 1

                var verify = await ui.InvokeAsync(() =>
                    $"stanice={vm.Board.StationName}, tab={vm.SelectedTab}, hint='{vm.Board.RefreshHint}', sec={vm.Board.SecondsUntilRefresh}").GetTask();
                EmitLog($"AUTOTEST-VERIFY: {verify}");
            }

            EmitLog("AUTOTEST: držím tab 1 ~150 s (čekám na 2× „ui-canary: refresh tick fired“).");
            await Task.Delay(150000, ct).ConfigureAwait(false);
            var final = await ui.InvokeAsync(() =>
                $"stanice={vm.Board.StationName}, tab={vm.SelectedTab}, hint='{vm.Board.RefreshHint}', sec={vm.Board.SecondsUntilRefresh}").GetTask();
            EmitLog($"AUTOTEST-VERIFY final: {final}");
            EmitLog("AUTOTEST: hotovo.");
        }
        catch (Exception ex)
        {
            EmitLog("AUTOTEST: chyba " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Environment.Exit(0);   // TEMP-DIAG: ukončit appku po autotestu
        }
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
            // TEMP: překrývající se/předčasný výběr (kliknutí dvakrát, klik během bootstrapu)
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
