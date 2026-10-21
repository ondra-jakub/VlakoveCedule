using System.Diagnostics;
using System.Text;

namespace VlakoveCedule.Net;

/// <summary>
/// HTTP vrstva přes systémový <c>curl</c> (TLS fingerprint tohoto serveru odmítá HttpClient SslStream).
/// curl je přítomen na Windows 10+, macOS i Linuxu. Drží cookie jar pro JSF session.
/// </summary>
public sealed class CurlHttp : IDisposable
{
    private readonly string _jarPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CurlHttp(string jarPath)
    {
        _jarPath = jarPath;
        var dir = Path.GetDirectoryName(_jarPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(_jarPath)) File.Delete(_jarPath);
    }

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static string FindCurl()
    {
        var name = OperatingSystem.IsWindows() ? "curl.exe" : "curl";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var ext = OperatingSystem.IsWindows() ? new[] { ".exe" } : Array.Empty<string>();
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cand = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(cand)) return cand;
                foreach (var e in ext)
                {
                    var c2 = cand + e;
                    if (File.Exists(c2)) return c2;
                }
            }
            catch { /* ignore */ }
        }
        var fallbacks = new[]
        {
            "/usr/bin/curl", "/usr/local/bin/curl", "/opt/homebrew/bin/curl",
            @"C:\Windows\System32\curl.exe",
        };
        foreach (var f in fallbacks)
            if (File.Exists(f)) return f;
        throw new InvalidOperationException("Nenalezen systémový curl. Nainstalujte curl (macOS/Linux standard, Windows 10+/Git).");
    }

    private static async Task<string> RunAsync(ProcessStartInfo psi, int timeoutSec, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var exited = await Task.Run(() => proc.WaitForExit(timeoutSec * 1000), ct).ConfigureAwait(false);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new IOException($"curl timeout ({timeoutSec}s).");
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0 && stdout.Length == 0)
            throw new IOException($"curl selhal (exit {proc.ExitCode}): {stderr.Trim()}");
        return stdout;
    }

    private ProcessStartInfo BaseArgs(int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindCurl(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var a = psi.ArgumentList;
        a.Add("-s");
        a.Add("--compressed");
        a.Add("-m"); a.Add(timeoutSec.ToString());
        a.Add("--connect-timeout"); a.Add("30");
        a.Add("-b"); a.Add(_jarPath);
        a.Add("-c"); a.Add(_jarPath);
        a.Add("-A"); a.Add(UserAgent);
        return psi;
    }

    public Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => _gate.RunAsync(
            () =>
            {
                var psi = BaseArgs(90);
                psi.ArgumentList.Add(url);
                return RunAsync(psi, 90, ct);
            }, ct);

    public async Task<string> PostFormAsync(string url, IReadOnlyList<KeyValuePair<string, string>> fields,
        IReadOnlyCollection<string>? headers = null, int timeoutSec = 120, CancellationToken ct = default)
    {
        var body = FormEncode(fields);
        var tmp = Path.Combine(Path.GetTempPath(), $"vlakovecedule_post_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
        try
        {
            return await _gate.RunAsync(async () =>
            {
                var psi = BaseArgs(timeoutSec);
                var a = psi.ArgumentList;
                a.Add("-H"); a.Add("Accept: application/xml, text/xml, */*; q=0.01");
                a.Add("-H"); a.Add("Content-Type: application/x-www-form-urlencoded; charset=UTF-8");
                a.Add("-H"); a.Add("Faces-Request: partial/ajax");
                a.Add("-H"); a.Add("X-Requested-With: XMLHttpRequest");
                a.Add("-H"); a.Add("Referer: https://www.spravazeleznic.cz/cestujici/infotabule");
                if (headers != null)
                    foreach (var h in headers)
                    {
                        a.Add("-H"); a.Add(h);
                    }
                a.Add("--data-binary"); a.Add("@" + tmp);
                a.Add(url);
                return await RunAsync(psi, timeoutSec, ct);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

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
        _gate.Dispose();
        try { if (File.Exists(_jarPath)) File.Delete(_jarPath); } catch { }
    }
}