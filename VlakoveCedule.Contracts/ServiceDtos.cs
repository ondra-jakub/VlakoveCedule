namespace VlakoveCedule.Contracts;

/// <summary>Neměnný snapshot tabule předávaný z Core do UI (po otevření stanice i po každé obnově).</summary>
public sealed record BoardSnapshot(
    string StationName,
    string? StationInfoUrl,
    IReadOnlyList<BoardRow> Departures,
    IReadOnlyList<BoardRow> Arrivals,
    DateTimeOffset LastUpdated,
    DateTimeOffset? NextRefreshAt);

/// <summary>Stav prefetch smyčky (načítání kompletního seznamu stanic pro vyhledávač).</summary>
public sealed record PrefetchState(bool IsPrefetching, string ProgressText);

/// <summary>Která část UI je aktivní – řídí pause-policy prefetch a obnovovací cyklus.</summary>
public enum ViewMode
{
    /// <summary>Záložka „Výběr stanic“.</summary>
    Picker,

    /// <summary>Záložka Odjezdy/Příjezdy (zobrazená tabule).</summary>
    Board,
}

/// <summary>Požadavek na otevření stanice. Otevírá se vždy podle názvu + bucketu (atomický tok);
/// ClientId se nepoužívá (vždy „“), neboť je platné jen do další změny serverového view.</summary>
public sealed record StationRequest(string StationName, string? Bucket, string? ClientId);

/// <summary>Výsledek hledání v katalogu stanic (max 200 položek + celkový počet shod).</summary>
public sealed record StationSearchResult(IReadOnlyList<Station> Stations, int TotalCount);
