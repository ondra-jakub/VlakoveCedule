using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VlakoveCedule.Models;
using VlakoveCedule.Scraping;

namespace VlakoveCedule.ViewModels;

public partial class AppViewModel : ObservableObject
{
    public const string DefaultStation = "Praha hlavní nádraží";

    private SzdcClient? _client;
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

    public BoardPageVm Board { get; private set; }
    public StationPickerVm Picker { get; private set; }

    public ObservableCollection<string> Tabs { get; } = ["Výběr stanic", "Odjezdy", "Příjezdy"];

    private readonly DispatcherTimer _clockTimer;

    public AppViewModel()
    {
        Board = new BoardPageVm(RefreshBoardAsync, EmitLog);
        Picker = new StationPickerVm(
            () => _client?.Letters.Select(l => new Models.LetterItem { ClientId = l.ClientId, Letter = l.Letter }).ToList() ?? [],
            (letter, ct) => _client!.LoadLetterAsync(letter, ct),
            _ => Task.CompletedTask);
        Picker.StationRequested += OnStationRequested;
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
            Picker.PrefetchPaused = false; // na Výběru stanic prefetch běží
        }
        else
        {
        Picker.PrefetchPaused = true;
        Board.StartAutoRefresh();
        // okamžitě obnovit jen když view na serveru není tabule (uživatel procházel stanice);
        // 0×0 tabule je legálně prázdná (málo vlaků), NE zastaralá – nechceme kvůli ní spouštět obnovu
        var stale = _client is null || !_client.IsViewBoard;
        if (stale) _ = Board.RefreshNowCommand.ExecuteAsync(null);
        }
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        IsBusy = true;
        BusyReason = "Připojuji se k Infotabuli…";
        try
        {
            var state = LoadState();
            _client = new SzdcClient();
            _client.Log += s =>
            {
                if (s.StartsWith("Písmeno ", StringComparison.Ordinal)) return;
                Status = s;
            };
            await _client.InitializeAsync(ct: ct).ConfigureAwait(true);
            Picker.RefreshLetterItems();

            var station = state?.Station is { Length: > 0 } st ? st : DefaultStation;
            BusyReason = $"Otevírám tabuli stanice {station}…";
            // POZOR – pořadí: nejdřív otevřít tabuli, poté spustit prefetch (jinak by kliky na písmena
            // přepsaly serverové view a rozbily hned první obnovu tabule).
            await SelectStationCoreAsync(station, bucket: null, clientId: null, ct).ConfigureAwait(true);
            SaveState(new AppState { Station = station });
            LastStationName = station;
            Status = "Připojeno. Tabule se obnovuje každých " + BoardPageVm.RefreshPeriodSeconds + " s.";
            Board.StartAutoRefresh();
            Picker.StartPrefetch();
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
        Picker.PrefetchPaused = true;
        IsBusy = true;
        BusyReason = $"Otevírám tabuli stanice {args.StationName}…";
        try
        {
            await SelectStationCoreAsync(args.StationName, args.Bucket, args.ClientId, CancellationToken.None).ConfigureAwait(true);
            SaveState(new AppState { Station = args.StationName });
            Board.StartAutoRefresh();
            SelectedTab = 1;
            Status = $"Tabule stanice {args.StationName}. Obnova každých {BoardPageVm.RefreshPeriodSeconds} s.";
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
            Picker.PrefetchPaused = SelectedTab != 0; // na Výběru stanic prefetch běží, na tabuli stojí
        }
    }

    private async Task SelectStationCoreAsync(string name, string? bucket, string? clientId, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Klient není připraven.");
        SzdcParser.BoardInfo board;
        if (!string.IsNullOrEmpty(clientId))
        {
            board = await client.SelectStationLinkAsync(clientId, name, ct).ConfigureAwait(true);
        }
        else
        {
            board = await client.SelectStationByNameAsync(name, bucket, ct).ConfigureAwait(true);
        }

        // Pojistka proti tichému „úspěchu“ špatné tabule: klik na staré/neexistující tlačítko stanice
        // vyřídí server v aktuálním (jiném) view a re-render vrátí tabuli předchozí stanice.
        if (board.StationName.Length > 0 && !SameStationName(board.StationName, name))
        {
            client.ResetView();         // klient nesmí dál vědět, že je otevřen špatná stanice
            EmitLog($"Výběr stanice: server otevřel „{board.StationName}“ místo „{name}“.");
            throw new SzdcParser.SzdcException(
                $"Server otevřel tabuli „{board.StationName}“ místo „{name}“. Zopakujte, prosím, výběr stanice.");
        }
        Board.Load(board);
        if (!string.IsNullOrEmpty(board.StationName))
            LastStationName = board.StationName;
    }

    private static bool SameStationName(string a, string b) =>
        string.Equals(
            Regex.Replace(a.Trim(), @"\s+", " "),
            Regex.Replace(b.Trim(), @"\s+", " "),
            StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string? _lastStationName;

    internal static void EmitLog(string message)
        => DiagLog.Append("flow.log", $"[VM] {message}");

    private async Task<(IReadOnlyList<BoardRow> dep, IReadOnlyList<BoardRow> arr)> RefreshBoardAsync(CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Klient není připraven.");
        try
        {
            EmitLog("refresh (poll)…");
            var r = await client.RefreshAsync(ct).ConfigureAwait(true);
            EmitLog($"refresh OK ({r.Departures.Count}/{r.Arrivals.Count}).");
            return (r.Departures, r.Arrivals);
        }
        catch (SzdcParser.StaleViewException)
        {
            // Uživatel mezi tím procházel stanice (view přepsáno) – NENÍ to vypršení session,
            // stačí tabuli poslední stanice znovu otevřít (bez reinicializace klienta).
            EmitLog("refresh: view zastaralé → znovuotevřu poslední stanici.");
            var name = LastStationName ?? client.CurrentStationName;
            if (string.IsNullOrEmpty(name)) throw;
            await SelectStationCoreAsync(name, Picker.BucketFor(name), null, ct).ConfigureAwait(true);
            var board = client.CurrentBoard
                ?? throw new InvalidOperationException("Tabule není otevřena.");
            EmitLog($"reopen OK ({board.Departures.Count}/{board.Arrivals.Count}).");
            return (board.Departures, board.Arrivals);
        }
        catch (Exception ex)
        {
            // session pravděpodobně vypršela → celá reinicializace + nový výběr poslední stanice
            EmitLog("refresh selhal: " + ex.Message);
            Status = "Session vypršela – znovu připojuji…";
            await ReconnectAsync(ct).ConfigureAwait(true);
            var board = client.CurrentBoard
                ?? throw new InvalidOperationException("Tabule není otevřena.");
            EmitLog("reconnect OK, tabule znovu otevřena.");
            return (board.Departures, board.Arrivals);
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Klient není připraven.");
        var resumePrefetch = !Picker.PrefetchPaused;
        Picker.PrefetchPaused = true;
        try
        {
            try { await client.InitializeAsync(ct: ct).ConfigureAwait(true); }
            catch (Exception ex) { Status = "Chyba připojení: " + ex.Message; throw; }
            Picker.RefreshLetterItems();
            if (LastStationName != null)
            {
                var bucket = Picker.BucketFor(LastStationName);
                await SelectStationCoreAsync(LastStationName, bucket, null, ct).ConfigureAwait(true);
            }
        }
        finally
        {
            if (resumePrefetch) Picker.PrefetchPaused = false;
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        IsBusy = true;
        BusyReason = "Obnovuji…";
        try
        {
            await ReconnectAsync(CancellationToken.None).ConfigureAwait(true);
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

    // ---------- perzistence ----------
    private sealed class AppState
    {
        public string? Station { get; set; }
    }

    private static string GetAppData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VlakoveCedule");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AppState? LoadState()
    {
        try
        {
            var f = Path.Combine(GetAppData(), "state.json");
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(f));
        }
        catch { return null; }
    }

    private static void SaveState(AppState state)
    {
        try
        {
            var f = Path.Combine(GetAppData(), "state.json");
            File.WriteAllText(f, JsonSerializer.Serialize(state));
        }
        catch { }
    }
}