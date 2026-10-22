using System.Net;
using System.Text.RegularExpressions;
using VlakoveCedule.Models;
using VlakoveCedule.Net;

namespace VlakoveCedule.Scraping;

/// <summary>
/// Klient Infotabule (www.spravazeleznic.cz). Replikuje JSF/PrimeFaces AJAX toky:
/// inicializace → písmeno/seznam stanic → klik na stanici → poll na listDepartures/listArrivals.
/// </summary>
public sealed class SzdcClient : IDisposable
{
    public const string BaseUrl = "https://www.spravazeleznic.cz/cestujici/infotabule";
    private const string Prefix = "_tabule_WAR_szdc_boards_web_";
    private const string FormId = Prefix + ":tabsForm";
    private const string ActiveIdx = Prefix + ":tabsForm:tabview_activeIndex";
    private const string FilterInput = Prefix + ":tabsForm:tabview:filterInput_input";

    private readonly SzdcHttp _http;
    private readonly SemaphoreSlim _flowGate = new(1, 1);
    private SzdcParser.PageInfo? _page;

    /// <summary>Nazev stanice → písmeno (bucket) — plní se prefetchem.</summary>
    private readonly Dictionary<string, string> _stationBuckets = new(StringComparer.OrdinalIgnoreCase);

    public string? CurrentStationName { get; private set; }
    public SzdcParser.BoardInfo? CurrentBoard { get; private set; }

    /// <summary>true, dokud je serverové view na tabuli (po kliku na stanici). Klik na písmeno ho mění na výběr stanic.</summary>
    public bool IsViewBoard { get; private set; }

    public event Action<string>? Log;

    private void Emit(string message)
    {
        Log?.Invoke(message);
        DiagLog.Append("flow.log", message);
    }

    public SzdcClient()
    {
        _http = new SzdcHttp();
    }

    /// <summary>Nacte uvodní stránku + detectScreen + startup init. Volat na zacatku a po vypršení session.</summary>
    public async Task InitializeAsync(string screenSize = "1600", CancellationToken ct = default)
    {
        await _flowGate.RunAsync(async () =>
        {
            var html = await _http.GetStringAsync(BaseUrl, ct).ConfigureAwait(false);
            _page = SzdcParser.ParseInitialPage(html);
            Emit($"Session inicializována (ViewState {_page.ViewState[..Math.Min(24, _page.ViewState.Length)]}…, písmen {_page.Letters.Count})");

            // 1. detectScreen
            await PostPartialAsync(new List<(string k, string v)>
            {
                ("javax.faces.partial.ajax", "true"),
                ("javax.faces.source", _page.DetectId),
                ("javax.faces.partial.execute", _page.DetectId),
                (_page.DetectId, _page.DetectId),
                ("screenSize", screenSize),
            }, baseFields: true).ConfigureAwait(false);

            // 2. startup init
            await PostPartialAsync(new List<(string k, string v)>
            {
                ("javax.faces.partial.ajax", "true"),
                ("javax.faces.source", _page.InitId),
                ("javax.faces.partial.execute", "@all"),
            }, baseFields: true, includeInit: true).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Písmena abecední lišty (A…Ž + Ch).</summary>
    public IReadOnlyList<(string ClientId, string Letter)> Letters =>
        _page?.Letters ?? [];

    private List<(string k, string v)> BaseFields(bool withForm, string activeIndex = "0") => new()
    {
        (FormId, FormId),
        (FilterInput, ""),
        (ActiveIdx, activeIndex),
    };

    private async Task<string> PostPartialAsync(List<(string k, string v)> jsfParams, bool baseFields,
        bool includeInit = false, string? vs = null, string? activeIndex = null)
    {
        var p = _page ?? throw new InvalidOperationException("Klient není inicializován.");
        var fields = new List<(string k, string v)>(jsfParams);
        if (includeInit)
            fields.Add((p.InitId, p.InitId));
        if (baseFields)
            fields.AddRange(BaseFields(withForm: true, activeIndex: activeIndex ?? "0"));
        fields.Add(("javax.faces.encodedURL", p.AjaxUrl));
        fields.Add(("javax.faces.ViewState", vs ?? p.ViewState));
        var kv = fields.Select(t => new KeyValuePair<string, string>(t.k, t.v)).ToList();
        var resp = await _http.PostFormAsync(p.AjaxUrl, kv).ConfigureAwait(false);
        var newVs = SzdcParser.ExtractViewStateUpdate(resp);
        if (newVs != null) _page = p with { ViewState = newVs };
        return resp;
    }

    /// <summary>Klik na písmeno → seznam stanic (čerstvé client-id tlačítka stanic).</summary>
    public Task<LetterListing> LoadLetterAsync(string letter, CancellationToken ct = default)
        => _flowGate.RunAsync(() => LoadLetterCoreAsync(letter, ct), ct);

    private async Task<LetterListing> LoadLetterCoreAsync(string letter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var p = _page ?? throw new InvalidOperationException("Klient není inicializován.");
        var btnId = p.Letters.FirstOrDefault(l => string.Equals(l.Letter, letter, StringComparison.OrdinalIgnoreCase)).ClientId
                    ?? throw new SzdcParser.SzdcException($"Písmeno '{letter}' není v seznamu.");
        var resp = await PostPartialAsync(new List<(string, string)>
        {
            ("javax.faces.partial.ajax", "true"),
            ("javax.faces.source", btnId),
            ("javax.faces.partial.execute", "@all"),
            ("javax.faces.partial.render", FormId),
            (btnId, btnId),
        }, baseFields: true).ConfigureAwait(false);
        var form = SzdcParser.ExtractFormUpdate(resp) ?? throw new SzdcParser.SzdcException("Server nevrátil seznam stanic (klik na písmeno selhal).");
        // písmenový formulář může obsahovat nová id tlačítek písmen → obnovit mapu
        var lettersNow = SzdcParser.ParseLetters(form).ToList();
        if (lettersNow.Count > 0)
            _page = p with { Letters = lettersNow.Select(kv => (kv.Value, kv.Key)).ToList() };
        var listing = SzdcParser.ParseStations(form, letter);
        foreach (var s in listing.Stations)
            _stationBuckets[s.Name] = letter;
        IsViewBoard = false;
        Emit($"Písmeno {letter}: {listing.Stations.Count} stanic.");
        return listing;
    }

    /// <summary>Klik na konkrétní tlačítko stanice (z čerstvého listingu) → tabule odjezdů/příjezdů.</summary>
    public Task<SzdcParser.BoardInfo> SelectStationLinkAsync(string stationClientId, string stationName, CancellationToken ct = default)
        => _flowGate.RunAsync(() => SelectStationLinkCoreAsync(stationClientId, stationName, ct), ct);

    private async Task<SzdcParser.BoardInfo> SelectStationLinkCoreAsync(string stationClientId, string stationName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resp = await PostPartialAsync(new List<(string, string)>
        {
            ("javax.faces.partial.ajax", "true"),
            ("javax.faces.source", stationClientId),
            ("javax.faces.partial.execute", "@all"),
            ("javax.faces.partial.render", FormId),
            (stationClientId, stationClientId),
        }, baseFields: true).ConfigureAwait(false);
        var form = SzdcParser.ExtractFormUpdate(resp) ?? throw new SzdcParser.SzdcException($"Stanici „{stationName}“ se nepodařilo otevřít (server nevrátil tabuli).");
        var board = SzdcParser.ParseBoard(form);
        board = board with { PollId = SzdcParser.ParsePollId(form) };
        CurrentBoard = board;
        CurrentStationName = board.StationName;
        IsViewBoard = true;
        Emit($"Tabule stanice {board.StationName}: {board.Departures.Count}× odjezd, {board.Arrivals.Count}× příjezd.");
        return board;
    }

    /// <summary>Výběr stanice podle názvu: atomicky klikne na písmeno a v čerstvém listingu najde stanici.</summary>
    public async Task<SzdcParser.BoardInfo> SelectStationByNameAsync(string name, string? knownBucket = null, CancellationToken ct = default)
    {
        return await _flowGate.RunAsync(async () =>
        {
            var bucket = knownBucket ?? GuessBucket(name);
            var listing = await LoadLetterCoreAsync(bucket, ct).ConfigureAwait(false);
            var link = FindInListing(listing, name);
            if (link == null)
            {
                foreach (var alt in AltBuckets(bucket))
                {
                    listing = await LoadLetterCoreAsync(alt, ct).ConfigureAwait(false);
                    link = FindInListing(listing, name);
                    if (link != null) break;
                }
            }
            if (link == null)
                throw new SzdcParser.SzdcException($"Stanice „{name}“ nenalezena v seznamu písmene {bucket}.");
            return await SelectStationLinkCoreAsync(link.ClientId, link.Name, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static Station? FindInListing(LetterListing listing, string name) =>
        listing.Stations.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Obnova tabule: poll (jako PrimeFaces na webu) → cílený update obou seznamů.</summary>
    public async Task<(IReadOnlyList<BoardRow> Departures, IReadOnlyList<BoardRow> Arrivals)> RefreshAsync(CancellationToken ct = default)
    {
        return await _flowGate.RunAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var board = CurrentBoard ?? throw new InvalidOperationException("Není otevřena žádná stanice.");
            var p = _page ?? throw new InvalidOperationException("Klient není inicializován.");
            if (!IsViewBoard)
                throw new SzdcParser.StaleViewException("View se mezi tím změnilo (výběr stanic) – nutné znovu otevřít stanici.");
            var depId = board.DeparturesListId ?? FormId + ":tabview:listDepartures";
            var arrId = board.ArrivalsListId ?? FormId + ":tabview:listArrivals";

            var resp = await PostPartialAsync(new List<(string, string)>
            {
                ("javax.faces.partial.ajax", "true"),
                ("javax.faces.source", board.PollId ?? ""),
                ("javax.faces.partial.execute", board.PollId ?? ""),
                ("javax.faces.partial.render", depId + " " + arrId),
                (board.PollId ?? "", board.PollId ?? ""),
            }, baseFields: true, activeIndex: "1").ConfigureAwait(false);

            var updates = SzdcParser.ExtractListUpdates(resp);
            IReadOnlyList<BoardRow> deps = board.Departures, arrs = board.Arrivals;
            foreach (var (id, content) in updates)
            {
                var rows = SzdcParser.ParseDatatable(content, id);
                if (id.EndsWith("listDepartures", StringComparison.Ordinal)) deps = rows;
                else if (id.EndsWith("listArrivals", StringComparison.Ordinal)) arrs = rows;
            }
            board = board with { Departures = deps, Arrivals = arrs };
            CurrentBoard = board;
            return (deps, arrs);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Heuristika bucketu podle prvního písmena názvu stanice (včetně skupiny „Ch“).</summary>
    public static string GuessBucket(string stationName)
    {
        var name = stationName.TrimStart();
        if (name.Length == 0) return "A";
        if (name.Length >= 2 && (name[0] is 'C' or 'c') && (name[1] is 'h' or 'H')) return "Ch";
        var c = char.ToUpper(name[0]);
        return c.ToString();
    }

    /// <summary>Zahodí stav otevřené tabule (po odhaleném nesouladu view) – další obnova pak stanici znovu otevře.</summary>
    public void ResetView()
    {
        CurrentBoard = null;
        CurrentStationName = null;
        IsViewBoard = false;
    }

    private static IEnumerable<string> AltBuckets(string bucket) => bucket switch
    {
        "C" => new[] { "Č", "Ch" },
        "Ch" => new[] { "C" },
        "Č" => new[] { "C" },
        "S" => new[] { "Š" },
        "Š" => new[] { "S" },
        "Z" => new[] { "Ž" },
        "Ž" => new[] { "Z" },
        "U" => new[] { "Ú" },
        "Ú" => new[] { "U" },
        _ => [],
    };

    public void Dispose()
    {
        _http.Dispose();
        _flowGate.Dispose();
    }
}