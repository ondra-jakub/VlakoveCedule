using System.Diagnostics;
using System.Text.RegularExpressions;
using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Scraping;

namespace VlakoveCedule.Core.Services;

/// <summary>
/// Aplikační logika Infotabule nezávislá na UI: životní cyklus <see cref="SzdcClient"/>, bootstrap,
/// výběr stanice, obnova tabule, prefetch katalogu stanic a perzistence poslední stanice.
/// Eventy mohou být vyvolány na libovolném (background) vlákně – UI si je povinně maršaluje samo.
/// </summary>
public sealed class InfotabuleService : IDisposable
{
    public const int RefreshPeriodSeconds = 60;
    public const string DefaultStation = "Praha hlavní nádraží";

    private readonly SzdcClient _client = new();
    private readonly StationCatalog _catalog = new();
    private readonly AppStateStore _store = new();

    private readonly object _stateLock = new();
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _cycleCts;
    private volatile bool _prefetchPaused;
    private volatile ViewMode _viewMode = ViewMode.Picker;
    private string _prefetchProgress = "";
    private long _nextRefreshTicks;      // 0 = obnovovací cyklus neběží
    private int _refreshing;             // 0/1 – reentrancy guard obnovy
    private string? _lastListedLetter;
    private bool _disposed;

    /// <summary>Diagnostické zprávy toku (do flow.log).</summary>
    public event Action<string>? Log;

    /// <summary>Produktový stavový text (spodní lišta).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Produktový text průběhu operace (BusyReason).</summary>
    public event Action<string>? BusyReasonChanged;

    /// <summary>Nová data tabule (po otevření stanice, obnově i znovuotevření).</summary>
    public event Action<BoardSnapshot>? BoardUpdated;

    /// <summary>Písmena abecední lišty (po inicializaci klienta).</summary>
    public event Action<IReadOnlyList<LetterItem>>? LettersChanged;

    /// <summary>Čerstvý seznam stanic pod písmenem (po kliku na písmeno).</summary>
    public event Action<LetterListing>? LetterListed;

    /// <summary>Stav prefetch smyčky.</summary>
    public event Action<PrefetchState>? PrefetchChanged;

    /// <summary>Obnova tabule selhala (odpočet se přesto resetuje).</summary>
    public event Action? RefreshFailed;

    public string? LastStationName { get; private set; }

    public bool IsViewBoard => _client.IsViewBoard;

    /// <summary>Okamžik příští automatické obnovy; null = cyklus neběží (výběr stanic).</summary>
    public DateTimeOffset? NextRefreshAt
    {
        get
        {
            var ticks = Volatile.Read(ref _nextRefreshTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public InfotabuleService()
    {
        _client.Log += OnClientLog;
    }

    /// <summary>Inicializace klienta + obnovení poslední stanice + spuštění smyček.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var state = _store.Load();
        await _client.InitializeAsync(ct: ct).ConfigureAwait(false);
        RaiseLettersChanged();

        var station = state?.Station is { Length: > 0 } st ? st : DefaultStation;
        BusyReasonChanged?.Invoke($"Otevírám tabuli stanice {station}…");
        // POZOR – pořadí: nejdřív otevřít tabuli, poté spustit prefetch (jinak by kliky na písmena
        // přepsaly serverové view a rozbily hned první obnovu tabule).
        await SelectStationCoreAsync(station, bucket: null, clientId: null, ct).ConfigureAwait(false);
        _store.Save(new AppStateStore.AppState { Station = station });
        LastStationName = station;
        StatusChanged?.Invoke("Připojeno. Tabule se obnovuje každých " + RefreshPeriodSeconds + " s.");
        StartRefreshCycle();
        StartPrefetchLoop();
    }

    /// <summary>Výběr stanice (vždy název + bucket), včetně pojistky proti tiché chybě serveru.
    /// Při selhání výjimku propaguje volajícímu.</summary>
    public async Task<BoardSnapshot> SelectStationAsync(StationRequest request, CancellationToken ct = default)
    {
        _prefetchPaused = true;   // během výběru stanice prefetch stojí (jinak by přepsal serverové view)
        try
        {
            var snapshot = await SelectStationCoreAsync(request.StationName, request.Bucket, request.ClientId, ct)
                .ConfigureAwait(false);
            _store.Save(new AppStateStore.AppState { Station = request.StationName });
            ResetCyclePhase();    // nová tabule → odpočet obnovy začíná znovu
            return snapshot;
        }
        finally
        {
            _prefetchPaused = _viewMode != ViewMode.Picker; // na Výběru stanic prefetch běží, na tabuli stojí
        }
    }

    /// <summary>Okamžitá obnova tabule; chyby se hlásí přes <see cref="RefreshFailed"/> a smyčku neshazují.</summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            EmitLog("RefreshNow: skip (u probíhá).");
            return;
        }
        EmitLog("RefreshNow: start…");
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
            ResetCyclePhase();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("refresh error: " + ex.Message);
            ResetCyclePhase();      // chybná obnova nesmí zamrznout odpočítávání
            EmitLog("RefreshNow: CHYBA " + ex.GetType().Name + ": " + ex.Message);
            RefreshFailed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Plná reinicializace klienta + znovuotevření poslední stanice (bez změny pauzy prefetch).</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        var resumePrefetch = !_prefetchPaused;
        _prefetchPaused = true;
        try
        {
            try { await _client.InitializeAsync(ct: ct).ConfigureAwait(false); }
            catch (Exception ex) { StatusChanged?.Invoke("Chyba připojení: " + ex.Message); throw; }
            RaiseLettersChanged();
            if (LastStationName != null)
                await SelectStationCoreAsync(LastStationName, BucketFor(LastStationName), null, ct).ConfigureAwait(false);
        }
        finally
        {
            if (resumePrefetch) _prefetchPaused = false;
        }
    }

    /// <summary>Přepnutí aktivní části UI → pause-policy prefetch + fáze obnovovacího cyklu.</summary>
    public void SetViewMode(ViewMode mode)
    {
        _viewMode = mode;
        if (mode == ViewMode.Picker)
        {
            _prefetchPaused = false; // na Výběru stanic prefetch běží
            StopRefreshCycle();
        }
        else
        {
            _prefetchPaused = true;
            StartRefreshCycle();
            // okamžitě obnovit jen když view na serveru není tabule (uživatel procházel stanice);
            // 0×0 tabule je legálně prázdná (málo vlaků), NE zastaralá – nechceme kvůli ní spouštět obnovu
            if (!_client.IsViewBoard)
                _ = RefreshNowAsync(CancellationToken.None);
        }
    }

    /// <summary>Klik na písmeno → čerstvý seznam stanic. Chyba se propaguje volajícímu.</summary>
    public async Task<LetterListing> ListLetterAsync(string letter, CancellationToken ct = default)
    {
        _lastListedLetter = letter;
        var listing = await _client.LoadLetterAsync(letter, ct).ConfigureAwait(false);
        LetterListed?.Invoke(listing);
        return listing;
    }

    /// <summary>Vyhledávání v katalogu stanic (naplněném prefetchem).</summary>
    public StationSearchResult Search(string query) => _catalog.Search(query);

    /// <summary>Bucket stanice z katalogu; fallback heuristika podle názvu (včetně skupiny „Ch“).</summary>
    public string BucketFor(string name) => _catalog.BucketFor(name) ?? SzdcClient.GuessBucket(name);

    // ---------- výběr stanice ----------

    private async Task<BoardSnapshot> SelectStationCoreAsync(string name, string? bucket, string? clientId, CancellationToken ct)
    {
        SzdcParser.BoardInfo board;
        if (!string.IsNullOrEmpty(clientId))
            board = await _client.SelectStationLinkAsync(clientId, name, ct).ConfigureAwait(false);
        else
            board = await _client.SelectStationByNameAsync(name, bucket, ct).ConfigureAwait(false);

        // Pojistka proti tichému „úspěchu“ špatné tabule: klik na staré/neexistující tlačítko stanice
        // vyřídí server v aktuálním (jiném) view a re-render vrátí tabuli předchozí stanice.
        if (board.StationName.Length > 0 && !SameStationName(board.StationName, name))
        {
            _client.ResetView();         // klient nesmí dál vědět, že je otevřen špatná stanice
            EmitLog($"Výběr stanice: server otevřel „{board.StationName}“ místo „{name}“.");
            throw new SzdcParser.SzdcException(
                $"Server otevřel tabuli „{board.StationName}“ místo „{name}“. Zopakujte, prosím, výběr stanice.");
        }
        var snapshot = ToSnapshot(board.Departures, board.Arrivals);
        if (!string.IsNullOrEmpty(board.StationName))
            LastStationName = board.StationName;
        BoardUpdated?.Invoke(snapshot);
        return snapshot;
    }

    private static bool SameStationName(string a, string b) =>
        string.Equals(
            Regex.Replace(a.Trim(), @"\s+", " "),
            Regex.Replace(b.Trim(), @"\s+", " "),
            StringComparison.OrdinalIgnoreCase);

    private BoardSnapshot ToSnapshot(IReadOnlyList<BoardRow> departures, IReadOnlyList<BoardRow> arrivals) =>
        new(_client.CurrentStationName ?? "",
            _client.CurrentBoard?.StationInfoUrl,
            departures,
            arrivals,
            DateTimeOffset.Now,
            NextRefreshAt);

    // ---------- obnova tabule ----------

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        try
        {
            EmitLog("refresh (poll)…");
            var r = await _client.RefreshAsync(ct).ConfigureAwait(false);
            EmitLog($"refresh OK ({r.Departures.Count}/{r.Arrivals.Count}).");
            BoardUpdated?.Invoke(ToSnapshot(r.Departures, r.Arrivals));
        }
        catch (SzdcParser.StaleViewException)
        {
            // Uživatel mezi tím procházel stanice (view přepsáno) – NENÍ to vypršení session,
            // stačí tabuli poslední stanice znovu otevřít (bez reinicializace klienta).
            EmitLog("refresh: view zastaralé → znovuotevřu poslední stanici.");
            var name = LastStationName ?? _client.CurrentStationName;
            if (string.IsNullOrEmpty(name)) throw;
            await SelectStationCoreAsync(name, BucketFor(name), null, ct).ConfigureAwait(false);
            var board = _client.CurrentBoard
                ?? throw new InvalidOperationException("Tabule není otevřena.");
            EmitLog($"reopen OK ({board.Departures.Count}/{board.Arrivals.Count}).");
        }
        catch (Exception ex)
        {
            // session pravděpodobně vypršela → celá reinicializace + nový výběr poslední stanice
            EmitLog("refresh selhal: " + ex.Message);
            StatusChanged?.Invoke("Session vypršela – znovu připojuji…");
            await ReconnectAsync(ct).ConfigureAwait(false);
            var board = _client.CurrentBoard
                ?? throw new InvalidOperationException("Tabule není otevřena.");
            EmitLog("reconnect OK, tabule znovu otevřena.");
        }
    }

    // ---------- obnovovací cyklus (dříve timer v BoardPageVm) ----------

    private void StartRefreshCycle()
    {
        lock (_stateLock)
        {
            _cycleCts?.Cancel();
            _cycleCts?.Dispose();
            SetCyclePhaseLocked(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(RefreshPeriodSeconds));
            _cycleCts = new CancellationTokenSource();
            var ct = _cycleCts.Token;
            _ = Task.Run(() => CycleAsync(ct));
        }
        EmitLog($"StartAutoRefresh (interval {RefreshPeriodSeconds}s, fáze se zarovnává).");
    }

    private void StopRefreshCycle()
    {
        lock (_stateLock)
        {
            _cycleCts?.Cancel();
            _cycleCts?.Dispose();
            _cycleCts = null;
            Volatile.Write(ref _nextRefreshTicks, 0);
        }
        EmitLog("Stop (cyklus obnovy zastaven).");
    }

    /// <summary>Reset fáze obnovy (nová tabule / dokončená obnova) – odpočet v UI čte NextRefreshAt.</summary>
    private void ResetCyclePhase()
    {
        lock (_stateLock)
        {
            if (_cycleCts is null) return;
            SetCyclePhaseLocked(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(RefreshPeriodSeconds));
        }
    }

    private void SetCyclePhaseLocked(DateTimeOffset at) => Volatile.Write(ref _nextRefreshTicks, at.UtcTicks);

    private async Task CycleAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // čekej na fázi obnovy; fáze se může během čekání resetovat → přepočítej
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var next = NextRefreshAt;
                    if (next is null) return;   // cyklus byl zastaven
                    var remaining = next.Value - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct)
                        .ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                EmitLog("ui-canary: refresh tick fired.");
                // obnova se nikdy nepřerušuje kvůli přepnutí záložky (stejně jako dnešní timer)
                await RefreshNowAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- prefetch katalogu stanic ----------

    private void StartPrefetchLoop()
    {
        lock (_stateLock)
        {
            _prefetchCts?.Cancel();
            _prefetchCts = new CancellationTokenSource();
            var ct = _prefetchCts.Token;
            _ = Task.Run(() => PrefetchLoopAsync(ct));
        }
    }

    private async Task PrefetchLoopAsync(CancellationToken ct)
    {
        RaisePrefetch(true, _prefetchProgress);
        try
        {
            var letters = _client.Letters.Select(l => l.Letter).ToList();
            for (var i = 0; i < letters.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (_prefetchPaused)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                RaisePrefetch(true, $"Načítám kompletní seznam stanic… ({i + 1}/{letters.Count})");
                try
                {
                    var listing = await _client.LoadLetterAsync(letters[i], ct).ConfigureAwait(false);
                    _catalog.Remember(listing);
                    if (string.Equals(_lastListedLetter, letters[i], StringComparison.Ordinal))
                        LetterListed?.Invoke(listing);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // jednotlivé písmeno nevadí
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            RaisePrefetch(true, $"Seznam stanic je kompletní ({_catalog.Count} stanic).");
        }
        catch (OperationCanceledException)
        {
            RaisePrefetch(true, "");
        }
        finally
        {
            RaisePrefetch(false, _prefetchProgress);
        }
    }

    private void RaisePrefetch(bool isPrefetching, string progressText)
    {
        _prefetchProgress = progressText;
        PrefetchChanged?.Invoke(new PrefetchState(isPrefetching, progressText));
    }

    // ---------- sdílené ----------

    private void RaiseLettersChanged()
    {
        var letters = _client.Letters
            .Select(l => new LetterItem { ClientId = l.ClientId, Letter = l.Letter })
            .ToList();
        LettersChanged?.Invoke(letters);
    }

    private void OnClientLog(string message)
    {
        if (message.StartsWith("Písmeno ", StringComparison.Ordinal)) return;
        StatusChanged?.Invoke(message);
    }

    private void EmitLog(string message) => Log?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_stateLock)
        {
            _cycleCts?.Cancel();
            _cycleCts?.Dispose();
            _cycleCts = null;
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
            _prefetchCts = null;
        }
        _client.Log -= OnClientLog;
        _client.Dispose();
    }
}
