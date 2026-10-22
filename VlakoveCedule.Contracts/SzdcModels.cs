namespace VlakoveCedule.Contracts;

/// <summary>Barva kategorie vlaku podle CSS webu Infotabule.</summary>
public enum TrainColor
{
    /// <summary>Os, Sp, Bus, TL, TLX… – tmavě modrá (#002b59).</summary>
    Navy,
    /// <summary>R, Rx – červená (#e60000).</summary>
    Red,
    /// <summary>LE, rj, EC, RJ, SC, IC, EN, nj, Ex, AEx – zelená (#10a764).</summary>
    Green,
    /// <summary>Ostatní – černá (výchozí CSS).</summary>
    Black,
}

/// <summary>Jeden řádek z odjezdové/příjezdové tabule nebo poznámka (narážka).</summary>
public sealed class BoardRow
{
    /// <summary>true = celošiřková poznámka (např. výluka), ostatní hodnoty prázdné.</summary>
    public bool IsNote;

    public string? Note;

    public string Scheduled = "";
    public TrainColor ScheduledColor = TrainColor.Navy;

    public string Actual = "";
    /// <summary>Aktuální čas je zpožděný (červený).</summary>
    public bool Delayed;

    /// <summary>Pouze u příjezdů: stanice, ze níž vlak přijíždí („Ze směru“).</summary>
    public string Origin = "";

    public string Train = "";
    public CompanySpan[] Company = [];
    public TrainColor TrainColor = TrainColor.Navy;

    public string Line = "";
    public TrainColor LineColor = TrainColor.Navy;

    public string Destination = "";

    public string Via = "";

    public string Platform = "";
    public TrainColor PlatformColor = TrainColor.Navy;
}

/// <summary>Údaj o dopravci v buňce Vlak (např. ČD, ARR, LEO, RJET).</summary>
public sealed class CompanySpan
{
    public required string Text;
}

/// <summary>Tabulka odjezdů/příjezdů pro jednu stanici.</summary>
public sealed class BoardData
{
    public required string StationName;
    /// <summary>URL na web Správy železnic s podrobnostmi o stanici (pokud známo).</summary>
    public string? StationInfoUrl;

    public required IReadOnlyList<BoardRow> Rows;
}

/// <summary>Stanice z výběru stanic.</summary>
public sealed record Station
{
    public required string Name { get; init; }
    /// <summary>JSF client id tlačítka stanice v aktuálním view – platné jen do další změny view.</summary>
    public required string ClientId { get; init; }
    /// <summary>Písmeno (bucket), pod nímž byla stanice v seznamu – např. „P“ nebo „Ch“.</summary>
    public required string Bucket { get; init; }
}

/// <summary>Výsledek kliknutí na písmeno – čerstvý seznam stanic + možné nové id tlačítek písmen.</summary>
public sealed class LetterListing
{
    public required string Bucket;
    public required IReadOnlyList<Station> Stations;
}
/// <summary>Položka abecední lišty (písmeno → client-id tlačítka).</summary>
public sealed record LetterItem
{
    public required string ClientId { get; init; }
    public required string Letter { get; init; }

    public override string ToString() => Letter;
}
