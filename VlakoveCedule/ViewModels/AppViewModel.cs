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
        Board = new BoardPageVm(RefreshBoardAsync);
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
        _ = BootstrapAsync(_lifeCts.Token);
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 0)
        {
            Board.Stop();
            Picker.PrefetchPaused = false; // na Výběru stanic prefetch běží
        }
        else
        {
            Picker.PrefetchPaused = true;
            Board.StartAutoRefresh();
            // okamžitě obnovit jen když je view zastaralé (uživatel mezi tím procházel stanice) nebo nic nemáme
            var stale = _client is null || !_client.IsViewBoard || !Board.HasData;
            if (stale) _ = Board.RefreshNowCommand.ExecuteAsync(null);
        }
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        IsBusy = true;
        BusyReason = "Připojuji se k Infotabuli…";
        try
        {
            var jar = Path.Combine(GetAppData(), "cookies.txt");
            var state = LoadState();
            _client = new SzdcClient(jar);
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
        }
        catch (Exception ex)
        {
            Status = "Chyba: " + ex.Message;
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
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VlakoveCedule");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "flow.log"), $"{DateTime.Now:HH:mm:ss.fff} [VM] {message}\n");
        }
        catch { }
    }

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
        catch (Exception ex)
        {
            // session pravděpodobně vypršela → celá reinicializace + nový výběr poslední stanice
            EmitLog("refresh selhal: " + ex.Message);
            Status = "Session vypršela – znovu připojuji…";
            await ReconnectAsync(ct).ConfigureAwait(true);
            var board = client.CurrentBoard
                ?? throw new InvalidOperationException("Tabule není otevřena.");
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