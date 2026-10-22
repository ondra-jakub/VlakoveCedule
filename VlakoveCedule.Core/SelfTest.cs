using VlakoveCedule.Contracts;
using VlakoveCedule.Core.Scraping;

namespace VlakoveCedule.Core;

/// <summary>Live ověření klienta z konzole: <c>dotnet run --project VlakoveCedule -- --probe</c></summary>
public static class SelfTest
{
    public static async Task RunAsync(string[] args)
    {
        using var client = new SzdcClient();
        client.Log += Console.WriteLine;
        var station = args.Length > 1 ? args[1] : "Praha hlavní nádraží";
        Console.WriteLine($"== Probe: {station} ==");
        await client.InitializeAsync();
        var bucket = SzdcClient.GuessBucket(station);
        Console.WriteLine($"Písmena: {string.Join(' ', client.Letters.Select(l => l.Letter))}");
        var listing = await client.LoadLetterAsync(bucket);
        Console.WriteLine($"Písmeno {bucket}: {listing.Stations.Count} stanic, první: {string.Join(", ", listing.Stations.Take(3).Select(s => s.Name))}");
        var link = listing.Stations.FirstOrDefault(s => s.Name.Equals(station.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new Exception($"Stanice '{station}' nenalezena pod písmenem {bucket}");
        var boardInfo = await client.SelectStationLinkAsync(link.ClientId, link.Name);
        Console.WriteLine($"Stanice: {boardInfo.StationName} (info: {boardInfo.StationInfoUrl ?? "-"})");
        Console.WriteLine($"Poll: {boardInfo.PollId ?? "-"}");
        Print("ODJEZDY", boardInfo.Departures);
        Print("PŘÍJEZDY", boardInfo.Arrivals);

        Console.WriteLine("== Refresh (poll) ==");
        var (deps, arrs) = await client.RefreshAsync();
        Console.WriteLine($"Refresh: {deps.Count} odjezdů, {arrs.Count} příjezdů.");
        Print("ODJEZDY po refreshi", deps);
        Console.WriteLine("== OK ==");
    }

    private static void Print(string title, IReadOnlyList<BoardRow> rows)
    {
        Console.WriteLine($"-- {title} ({rows.Count} řádků) --");
        foreach (var r in rows.Take(8))
        {
            if (r.IsNote)
            {
                Console.WriteLine($"  • POZNÁMKA: {r.Note}");
                continue;
            }
            var comp = r.Company is { Length: > 0 } ? " " + string.Join(' ', r.Company.Select(c => c.Text)) : "";
            var act = r.Actual.Length > 0 ? $" (→ {r.Actual}{(r.Delayed ? " ⏱" : "")})" : "";
            var where = r.Origin.Length > 0 ? $"z {r.Origin} " : $"do {r.Destination} ";
            Console.WriteLine($"  [{r.Scheduled}{act}] {where}{r.Train}{comp} | {r.Line} | via {r.Via} | {r.Platform}");
        }
        if (rows.Count > 8) Console.WriteLine($"  … a {rows.Count - 8} dalších");
    }
}