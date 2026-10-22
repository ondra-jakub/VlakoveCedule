using System.Net;
using System.Text.RegularExpressions;
using VlakoveCedule.Contracts;

namespace VlakoveCedule.Core.Scraping;

/// <summary>
/// Parsování Liferay/JSF/PrimeFaces odpovědí tabule (www.spravazeleznic.cz/cestujici/infotabule).
/// Vstupem je buď plná HTML stránka, nebo PrimeFaces partial-response XML.
/// </summary>
public static partial class SzdcParser
{
    private const string Prefix = "_tabule_WAR_szdc_boards_web_";
    public const string FormId = Prefix + ":tabsForm";

    public sealed record PageInfo
    {
        public required string ViewState;
        public required string AjaxUrl;      // javax.faces.encodedURL (životní cyklus 2 + JSF bridge)
        public required string DetectId;     // id remoteCommand pro detectScreen
        public required string InitId;       // id startup commandu
        public required List<(string ClientId, string Letter)> Letters;
    }

    public sealed record BoardInfo
    {
        public required string StationName;
        public string? StationInfoUrl;
        public required IReadOnlyList<BoardRow> Departures;
        public required IReadOnlyList<BoardRow> Arrivals;
        public string? PollId;
        public string? DeparturesListId;
        public string? ArrivalsListId;
    }

    // ---------- regulární výrazy ----------
    private static readonly Regex RxViewState = new(
        @"javax\.faces\.ViewState"" id=""javax\.faces\.ViewState"" value=""(?<vs>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex RxViewStateUpdate = new(
        @"<update[^>]*id=""javax\.faces\.ViewState""[^>]*><!\[CDATA\[(?<vs>[^\]]*)\]\]></update>",
        RegexOptions.Compiled);

    private static readonly Regex RxEncodedUrl = new(
        @"name=""javax\.faces\.encodedURL"" value=""(?<u>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex RxDetectCmd = new(
        @"detectScreenCommand\s*=\s*function\(\)\s*\{PrimeFaces\.ab\(\{s:""(?<id>[^""""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex RxStartupJid = new(
        @"command__tabule_WAR_szdc_boards_web__tabsForm_tabview_(?<jid>j_idt\d+)\s*=\s*function",
        RegexOptions.Compiled);

    private static readonly Regex RxLetterButtons = new(
        @"<button[^>]*name=""(?<id>_tabule[^""]*(j_idt\d+))""[^>]*>\s*<span class=""ui-button-text ui-c"">(?<txt>[^<]{1,3})</span>",
        RegexOptions.Compiled);

    private static readonly Regex RxStationLinks = new(
        @"<a id=""(?<id>[^""]+)"" href=""#"" class=""(?<cls>[^""]*boards-stations-list[^""]*)""[^>]*>(?<txt>[^<]*)</a>",
        RegexOptions.Compiled);

    private static readonly Regex RxPollWidget = new(
        @"PrimeFaces\.cw\(""Poll"",""[^""]*"",\{id:""(?<id>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex RxListId = new(
        @"id=""(?<id>[^""]*:(?:listDepartures|listArrivals))""",
        RegexOptions.Compiled);

    private static readonly Regex RxUpdateForm = new(
        @"<update[^>]*id=""" + Regex.Escape(FormId) + @"""[^>]*><!\[CDATA\[(?<html>.*?)\]\]></update>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RxTr = new(
        @"<tr\b(?<attrs>[^>]*)>(?<body>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RxTd = new(
        @"<td\b(?<attrs>[^>]*)>(?<body>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RxSpan = new(
        @"<span\b[^>]*class=""(?<cls>[^""]*)""[^>]*>(?<txt>[^<]*)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ---------- inicialní stránka ----------
    public static PageInfo ParseInitialPage(string html)
    {
        var vs = RxViewState.Match(html);
        if (!vs.Success) throw new SzdcException("Stránka neobsahuje ViewState – možná změna webu nebo blokace.");
        var encUrl = RxEncodedUrl.Match(html);
        if (!encUrl.Success) throw new SzdcException("Stránka neobsahuje encodedURL – nečekaná struktura.");
        var ajaxUrl = WebUtility.HtmlDecode(encUrl.Groups["u"].Value).Replace("&amp;", "&");

        string detectId;
        {
            var dm = RxDetectCmd.Match(html);
            detectId = dm.Success ? dm.Groups["id"].Value : FormId + ":j_idt3";
        }
        string initId;
        var sm = RxStartupJid.Match(html);
        initId = sm.Success ? FormId + ":tabview:" + sm.Groups["jid"].Value : FormId + ":tabview:j_idt10";

        var letters = new List<(string ClientId, string Letter)>();
        foreach (Match m in RxLetterButtons.Matches(html))
        {
            var id = m.Groups["id"].Value;
            var txt = WebUtility.HtmlDecode(m.Groups["txt"].Value).Trim();
            if (txt.Length == 1 || (txt.Length == 2 && txt == "Ch")) letters.Add((id, txt));
        }
        return new PageInfo { ViewState = vs.Groups["vs"].Value, AjaxUrl = ajaxUrl, DetectId = detectId, InitId = initId, Letters = letters };
    }

    // ---------- partial-response ----------
    public static string? ExtractViewStateUpdate(string resp)
    {
        var m = RxViewStateUpdate.Match(resp);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["vs"].Value).Trim() : null;
    }

    public static string? ExtractFormUpdate(string resp)
    {
        var m = RxUpdateForm.Match(resp);
        return m.Success ? m.Groups["html"].Value : null;
    }

    public static List<(string Id, string Content)> ExtractListUpdates(string resp)
    {
        var res = new List<(string, string)>();
        var i = 0;
        while (true)
        {
            var start = resp.IndexOf("<update", i, StringComparison.Ordinal);
            if (start < 0) break;
            var idm = new Regex(@"<update[^>]*id=""(?<id>[^""]*)""[^>]*><!\[CDATA\[", RegexOptions.Compiled).Match(resp, start);
            if (!idm.Success || idm.Index != start) { i = start + 7; continue; }
            var cstart = idm.Index + idm.Length;
            var cend = resp.IndexOf("]]></update>", cstart, StringComparison.Ordinal);
            if (cend < 0) break;
            var id = idm.Groups["id"].Value;
            if (id.Contains("listDepartures") || id.Contains("listArrivals") ||
                id.EndsWith("listDepartures", StringComparison.Ordinal) || id.EndsWith("listArrivals", StringComparison.Ordinal))
                res.Add((id, resp[cstart..cend]));
            i = cend + 12;
        }
        return res;
    }

    // ---------- formulář: písmena, stanice ----------
    public static Dictionary<string, string> ParseLetters(string formHtml)
    {
        var dict = new Dictionary<string, string>();
        foreach (Match m in RxLetterButtons.Matches(formHtml))
        {
            var txt = WebUtility.HtmlDecode(m.Groups["txt"].Value).Trim();
            if (txt.Length == 1 || (txt.Length == 2 && txt == "Ch")) dict[txt] = m.Groups["id"].Value;
        }
        return dict;
    }

    public static LetterListing ParseStations(string formHtml, string bucket)
    {
        var list = new List<Station>();
        foreach (Match m in RxStationLinks.Matches(formHtml))
        {
            var name = WebUtility.HtmlDecode(m.Groups["txt"].Value).Trim();
            if (name.Length == 0) continue;
            list.Add(new Station { Name = name, ClientId = m.Groups["id"].Value, Bucket = bucket });
        }
        return new LetterListing { Bucket = bucket, Stations = list };
    }

    public static string? ParsePollId(string formHtml) => RxPollWidget.Match(formHtml) is { Success: true } m ? m.Groups["id"].Value : null;

    public static (string? dep, string? arr) ParseListIds(string formHtml)
    {
        var m = RxListId.Matches(formHtml);
        string? dep = null, arr = null;
        foreach (Match x in m)
        {
            var id = x.Groups["id"].Value;
            if (id.EndsWith(":listDepartures", StringComparison.Ordinal)) dep ??= id;
            if (id.EndsWith(":listArrivals", StringComparison.Ordinal)) arr ??= id;
        }
        return (dep, arr);
    }

    // ---------- tabule ----------
    public static BoardInfo ParseBoard(string formHtml)
    {
        var (name, infoUrl) = ParseStationHeader(formHtml);
        var (depId, arrId) = ParseListIds(formHtml);
        var deps = depId != null ? ParseDatatable(formHtml, depId) : [];
        var arrs = arrId != null ? ParseDatatable(formHtml, arrId) : [];
        if (deps.Count == 0 && arrs.Count == 0)
        {
            // zkusit obecnou tabulku – oba seznamy najít podle obsahu hlavičky
        }
        return new BoardInfo
        {
            StationName = name,
            StationInfoUrl = infoUrl,
            Departures = deps,
            Arrivals = arrs,
            DeparturesListId = depId,
            ArrivalsListId = arrId,
        };
    }

    public static (string name, string? url) ParseStationHeader(string formHtml)
    {
        var i = formHtml.IndexOf("boards-name-label", StringComparison.Ordinal);
        if (i < 0) return ("", null);
        var start = formHtml.IndexOf('>', i);
        if (start < 0 || start >= formHtml.Length - 1) return ("", null);
        var end = formHtml.IndexOf("</label>", start, StringComparison.Ordinal);
        if (end < 0) return ("", null);
        var attrs = formHtml[Math.Max(0, i - 400)..start];
        var open = attrs.IndexOf("window.open('", StringComparison.Ordinal);
        string? url = null;
        if (open >= 0)
        {
            var from = open + "window.open('".Length;
            var to = attrs.IndexOf('\'', from);
            if (to > from) url = attrs[from..to];
        }
        var name = StripTags(formHtml[(start + 1)..end]);
        return (name, url);
    }

    /// <summary>Parsování jedné datatable (fragment update, nebo hledaný substrom formuláře).</summary>
    public static List<BoardRow> ParseDatatable(string html, string listId)
    {
        var fragment = ExtractFragment(html, listId);
        if (fragment == null) return [];
        var isArrivals = listId.EndsWith("listArrivals", StringComparison.Ordinal);
        var rows = new List<BoardRow>();
        foreach (Match tr in RxTr.Matches(fragment))
        {
            var attrs = tr.Groups["attrs"].Value;
            var body = tr.Groups["body"].Value;
            if (!attrs.Contains("data-ri=") && !body.Contains("gridcell")) continue;

            var tds = RxTd.Matches(body).Cast<Match>().ToList();
            if (tds.Count == 0) continue;

            // poznámka (colspan na celou šířku)
            if (tds.Count == 1 && tds[0].Groups["attrs"].Value.Contains("colspan"))
            {
                var note = StripTags(tds[0].Groups["body"].Value);
                if (note.Length > 0)
                    rows.Add(new BoardRow { IsNote = true, Note = note });
                continue;
            }

            var row = new BoardRow();

            if (isArrivals)
            {
                // Ze směru | Linka | Pravidelný | Aktuální | Vlak | Nástupiště
                if (tds.Count < 6) continue;
                row.Origin = Strip(WebUtility.HtmlDecode(string.Concat(TdTexts(tds[0]))));
                (row.Line, row.LineColor) = SingleColored(tds[1], TrainColor.Navy);
                ParseTimeCell(Spans(tds[2]), ref row.Scheduled, ref row.ScheduledColor);
                var actualSpans = Spans(tds[3]);
                row.Actual = Strip(WebUtility.HtmlDecode(actualSpans.Count > 0 ? actualSpans[0].txt : ""));
                row.Delayed = actualSpans.Count > 0 && actualSpans[0].cls.Contains("delay");
                ParseTrainCell(tds[4], row);
                (row.Platform, row.PlatformColor) = SingleColored(tds[5], row.TrainColor);
            }
            else
            {
                // Pravidelný | Aktuální | Vlak | Linka | Cíl | Přes | Nástupiště
                if (tds.Count < 6) continue;
                ParseTimeCell(Spans(tds[0]), ref row.Scheduled, ref row.ScheduledColor);
                var actualSpans = Spans(tds[1]);
                row.Actual = Strip(WebUtility.HtmlDecode(actualSpans.Count > 0 ? actualSpans[0].txt : ""));
                row.Delayed = actualSpans.Count > 0 && actualSpans[0].cls.Contains("delay");
                ParseTrainCell(tds[2], row);
                if (tds.Count > 3) (row.Line, row.LineColor) = SingleColored(tds[3], row.TrainColor);
                if (tds.Count > 4) row.Destination = Strip(WebUtility.HtmlDecode(string.Concat(TdTexts(tds[4]))));
                if (tds.Count > 5) row.Via = Strip(WebUtility.HtmlDecode(string.Concat(TdTexts(tds[5]))));
                if (tds.Count > 6) (row.Platform, row.PlatformColor) = SingleColored(tds[6], row.TrainColor);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string StripTags(string html)
        => Strip(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));

    private static string Strip(string s) => s.Trim().Trim('\t', '\n', '\r');

    private static string? ExtractFragment(string html, string id)
    {
        var needle = $"id=\"{id}\"";
        var i = html.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        // fragment = div.ui-datatable … najdeme hranici: od <div … id="..." /> na odpoutání se od rodičovské úrovně vezmeme celé <table></table>
        var tableStart = html.IndexOf("<table", i, StringComparison.Ordinal);
        if (tableStart < 0) tableStart = i;
        var end = html.IndexOf("</table>", tableStart, StringComparison.Ordinal);
        if (end < 0) return null;
        return html[tableStart..(end + 8)];
    }

    private static List<(string cls, string txt)> Spans(Match td)
    {
        var list = new List<(string, string)>();
        foreach (Match m in RxSpan.Matches(td.Groups["body"].Value))
            list.Add((m.Groups["cls"].Value, m.Groups["txt"].Value));
        return list;
    }

    private static IEnumerable<string> TdTexts(Match td)
    {
        var body = td.Groups["body"].Value;
        var spans = Spans(td);
        if (spans.Count == 0)
            yield return body;
        else
        {
            foreach (var s in spans) yield return Strip(WebUtility.HtmlDecode(s.txt)) + " ";
        }
    }

    private static void ParseTimeCell(List<(string cls, string txt)> spans, ref string text, ref TrainColor color)
    {
        if (spans.Count == 0) { text = ""; return; }
        var (cls, txt) = spans[0];
        text = Strip(WebUtility.HtmlDecode(txt));
        color = cls.Contains("delay") ? TrainColor.Red : ColorFromCss(cls, TrainColor.Black);
    }

    private static void ParseTrainCell(Match td, BoardRow row)
    {
        var spans = Spans(td);
        var texts = new List<string>();
        var color = TrainColor.Black;
        var companies = new List<CompanySpan>();
        foreach (var (cls, txt) in spans)
        {
            var t = Strip(WebUtility.HtmlDecode(txt));
            if (t.Length == 0) continue;
            if (cls.Contains("company"))
            {
                companies.Add(new CompanySpan { Text = t });
            }
            else
            {
                texts.Add(t);
                color = ColorFromCss(cls, color);
            }
        }
        row.Train = string.Join(" ", texts);
        row.TrainColor = color;
        row.Company = companies.ToArray();
    }

    private static (string text, TrainColor color) SingleColored(Match td, TrainColor fallback)
    {
        var spans = Spans(td);
        if (spans.Count == 0)
            return (Strip(WebUtility.HtmlDecode(td.Groups["body"].Value)), fallback);
        var (cls, txt) = spans[0];
        return (Strip(WebUtility.HtmlDecode(txt)), ColorFromCss(cls, fallback));
    }

    /// <summary>CSS → barva (podle stylech webu). Výchozí: defaultColor.</summary>
    public static TrainColor ColorFromCss(string cls, TrainColor defaultColor = TrainColor.Black)
    {
        if (string.IsNullOrEmpty(cls)) return defaultColor;
        var tokens = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            switch (token)
            {
                case "delay":
                    return TrainColor.Red;
                case "trainR" or "trainRx":
                    return TrainColor.Red;
                case "trainLE" or "trainrj" or "trainEC" or "trainRJ" or "trainSC" or "trainIC" or "trainEN" or "trainnj" or "trainEx" or "trainAEx":
                    return TrainColor.Green;
                case "trainOs" or "trainSp" or "trainBUS" or "trainBus" or "trainTL" or "trainTLX":
                    return TrainColor.Navy;
            }
        }
        return defaultColor;
    }

    public class SzdcException : Exception
    {
        public SzdcException(string message) : base(message) { }
    }

    /// <summary>Serverové view se změnilo (uživatel procházel stanice) – NENÍ to vypršení session,
    /// tabuli stačí znovu otevřít, bez inicializace celého klienta.</summary>
    public sealed class StaleViewException : SzdcException
    {
        public StaleViewException(string message) : base(message) { }
    }
}