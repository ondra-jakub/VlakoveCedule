using Microsoft.Maui.Graphics;
using VlakoveCedule.Contracts;

namespace VlakoveCedule.Maui.ViewModels;

/// <summary>Mapování TrainColor → Color podle palety webu (port BrushMap z desktopové verze).</summary>
public static class TrainPaint
{
    private static readonly Color Navy = Color.FromArgb("#002b59");
    private static readonly Color Red = Color.FromArgb("#e60000");
    private static readonly Color Green = Color.FromArgb("#10a764");
    private static readonly Color Black = Color.FromArgb("#000000");

    public static Color For(TrainColor c) => c switch
    {
        TrainColor.Navy => Navy,
        TrainColor.Red => Red,
        TrainColor.Green => Green,
        _ => Black,
    };
}

/// <summary>ViewModel řádku tabule (nebo poznámky/výluky). Barvy jako typu Color – bindingem se
/// přímo napojují na elementové vlastnosti (TextColor / BackgroundColor).</summary>
public sealed class BoardRowVm
{
    private static readonly Color RowWhite = Colors.White;
    private static readonly Color RowGray = Color.FromArgb("#f0f0f0");

    public bool IsNote { get; init; }
    public string Note => IsNote ? NoteText ?? "" : "";
    public string? NoteText { get; init; }

    /// <summary>Řádek s vlakem (ne poznámka).</summary>
    public bool IsRegular => !IsNote;

    /// <summary>Buňka Aktuální má hodnotu (jinak prázdná – vlak jede podle jízdního řádu).</summary>
    public bool HasActual => !string.IsNullOrEmpty(Actual);

    /// <summary>Kompaktní rozvržení: „Přes“ vykreslit jen když něco obsahuje.</summary>
    public bool HasVia => !string.IsNullOrEmpty(Via);

    public string Scheduled { get; init; } = "";
    public Color ScheduledBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public string Actual { get; init; } = "";
    public Color ActualBrush { get; init; } = TrainPaint.For(TrainColor.Red);

    public string Train { get; init; } = "";
    public string CompanyText { get; init; } = "";
    public Color TrainBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    /// <summary>Pouze u příjezdů: stanice, ze níž vlak přijíždí („Ze směru“).</summary>
    public string Origin { get; init; } = "";

    public string Line { get; init; } = "";
    public Color LineBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public string Destination { get; init; } = "";

    public string Via { get; init; } = "";

    public string Platform { get; init; } = "";
    public Color PlatformBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public bool Delayed { get; init; }

    public Color RowBackground { get; init; } = RowWhite;
    public Color NoteTextBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public static BoardRowVm From(BoardRow r, bool oddRow)
    {
        var bg = oddRow ? RowGray : RowWhite;
        if (r.IsNote)
            return new BoardRowVm { IsNote = true, NoteText = r.Note, RowBackground = bg };
        return new BoardRowVm
        {
            Scheduled = r.Scheduled,
            ScheduledBrush = TrainPaint.For(r.ScheduledColor),
            Actual = r.Actual,
            ActualBrush = TrainPaint.For(TrainColor.Red),
            Train = r.Train,
            CompanyText = r.Company is { Length: > 0 } ? string.Join(' ', r.Company.Select(c => c.Text)) : "",
            TrainBrush = TrainPaint.For(r.TrainColor),
            Line = r.Line,
            LineBrush = TrainPaint.For(r.LineColor),
            Destination = r.Destination,
            Origin = r.Origin,
            Via = r.Via,
            Platform = r.Platform,
            PlatformBrush = TrainPaint.For(r.PlatformColor),
            Delayed = r.Delayed,
            RowBackground = bg,
        };
    }
}