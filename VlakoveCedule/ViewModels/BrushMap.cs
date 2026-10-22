using Avalonia.Media;
using VlakoveCedule.Contracts;

namespace VlakoveCedule.ViewModels;

/// <summary>Mapování TrainColor → IBrush podle palety webu.</summary>
public static class TrainPaint
{
    private static readonly SolidColorBrush Navy = new() { Color = Color.Parse("#002b59") };
    private static readonly SolidColorBrush Red = new() { Color = Color.Parse("#e60000") };
    private static readonly SolidColorBrush Green = new() { Color = Color.Parse("#10a764") };
    private static readonly SolidColorBrush Black = new() { Color = Color.Parse("#000000") };

    public static IBrush For(TrainColor c) => c switch
    {
        TrainColor.Navy => Navy,
        TrainColor.Red => Red,
        TrainColor.Green => Green,
        _ => Black,
    };
}

/// <summary>ViewModel řádku tabule (nebo poznámky/výluky).</summary>
public sealed class BoardRowVm
{
    private static readonly SolidColorBrush RowWhite = new(Colors.White);
    private static readonly SolidColorBrush RowGray = new() { Color = Color.Parse("#f0f0f0") };

    public bool IsNote { get; init; }
    public string Note => IsNote ? NoteText ?? "" : "";
    public string? NoteText { get; init; }

    /// <summary>Řádek s vlakem (ne poznámka).</summary>
    public bool IsRegular => !IsNote;

    /// <summary>Buňka Aktuální má hodnotu (jinak prázdná – vlak jede podle jízdního řádu).</summary>
    public bool HasActual => !string.IsNullOrEmpty(Actual);

    public string Scheduled { get; init; } = "";
    public IBrush ScheduledBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public string Actual { get; init; } = "";
    public IBrush ActualBrush { get; init; } = TrainPaint.For(TrainColor.Red);

    public string Train { get; init; } = "";
    public string CompanyText { get; init; } = "";
    public IBrush TrainBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    /// <summary>Pouze u příjezdů: stanice, ze níž vlak přijíždí („Ze směru“).</summary>
    public string Origin { get; init; } = "";

    public string Line { get; init; } = "";
    public IBrush LineBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public string Destination { get; init; } = "";

    public string Via { get; init; } = "";

    public string Platform { get; init; } = "";
    public IBrush PlatformBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

    public bool Delayed { get; init; }

    public IBrush RowBackground { get; init; } = RowWhite;
    public IBrush NoteTextBrush { get; init; } = TrainPaint.For(TrainColor.Navy);

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