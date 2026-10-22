using System.Net;
using System.Net.Http;
using System.Text;

namespace VlakoveCedule.Net;

/// <summary>
/// HTTP vrstva pro Infotabuli. TLS tunel staví <see cref="BouncyTls"/> (server vyžaduje TLS 1.3),
/// HttpClient nad ním proto posílá plaintextový HTTP/1.1. Cookie jar drží JSF session.
/// </summary>
public sealed class SzdcHttp : IDisposable
{
    private const int HttpsPort = 443;
    private const string InfoTabuleReferer = "https://www.spravazeleznic.cz/cestujici/infotabule";
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string UserAgent { get; set; } = DefaultUserAgent;

    public SzdcHttp()
    {
        _handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = static (context, ct) =>
                new ValueTask<Stream>(BouncyTls.ConnectAsync(context.DnsEndPoint.Host, HttpsPort, ct)),
        };
        _http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => _gate.RunAsync(
            () => SendAsync(new Uri(url, UriKind.Absolute), HttpMethod.Get, null, [], 90, ct), ct);

    public Task<string> PostFormAsync(string url, IReadOnlyList<KeyValuePair<string, string>> fields,
        IReadOnlyCollection<string>? headers = null, int timeoutSec = 120, CancellationToken ct = default)
    {
        var body = FormEncode(fields);
        return _gate.RunAsync(async () =>
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type",
                "application/x-www-form-urlencoded; charset=UTF-8");
            var postHeaders = new List<string>
            {
                "Accept: application/xml, text/xml, */*; q=0.01",
                "Faces-Request: partial/ajax",
                "X-Requested-With: XMLHttpRequest",
                $"Referer: {InfoTabuleReferer}",
            };
            if (headers != null) postHeaders.AddRange(headers);
            return await SendAsync(new Uri(url, UriKind.Absolute), HttpMethod.Post, content, postHeaders,
                timeoutSec, ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task<string> SendAsync(Uri publicUri, HttpMethod method, HttpContent? content,
        IReadOnlyCollection<string> headers, int timeoutSec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            var currentUri = publicUri;
            var currentMethod = method;
            var currentContent = content;

            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(currentMethod, ToInternalUri(currentUri))
                {
                    Content = currentContent,
                };
                request.Headers.ConnectionClose = true;
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (headers.Count == 0)
                    request.Headers.TryAddWithoutValidation("Accept",
                        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                var cookie = _cookies.GetCookieHeader(currentUri);
                if (!string.IsNullOrEmpty(cookie))
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);

                foreach (var header in headers)
                {
                    var colon = header.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = header[..colon].Trim();
                    var value = header[(colon + 1)..].Trim();
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        currentContent?.Headers.TryAddWithoutValidation(name, value);
                    else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                        request.Headers.Referrer = new Uri(value, UriKind.Absolute);
                    else
                        request.Headers.TryAddWithoutValidation(name, value);
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    timeout.Token).ConfigureAwait(false);

                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        try { _cookies.SetCookies(currentUri, setCookie); }
                        catch (CookieException) { }
                    }
                }

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (redirects >= 5)
                        throw new HttpRequestException("Příliš mnoho přesměrování.");
                    if (currentContent != null)
                        throw new HttpRequestException("Neočekávané přesměrování po odeslání dat.");

                    var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!string.Equals(next.Host, publicUri.Host, StringComparison.OrdinalIgnoreCase))
                        throw new HttpRequestException($"Přesměrování na cizí hostitel: {next.Host}");

                    currentUri = next;
                    currentMethod = HttpMethod.Get;
                    continue;
                }

                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"HTTP timeout ({timeoutSec}s).");
        }
    }

    private static Uri ToInternalUri(Uri uri) =>
        new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = 80 }.Uri;

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>URL-kódování odpovídající JavaScript XHR (encodeURIComponent): dvojtečky i @ se kódují.</summary>
    internal static string FormEncode(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var f in fields)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(f.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(f.Value));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _gate.Dispose();
    }
}
