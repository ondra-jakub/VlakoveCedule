using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace VlakoveCedule.Base;

/// <summary>
/// TLS klient přes BouncyCastle. Server Infotabule vyžaduje TLS 1.3, který systémový
/// <see cref="SslStream"/> na macOS neumí (TLS 1.2 odmítá).
/// </summary>
internal static class BouncyTls
{
    public static async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            using var registration = ct.Register(static state =>
            {
                try { ((TcpClient)state!).Close(); } catch { }
            }, tcp);

            var protocol = await Task.Run(() =>
            {
                var p = new TlsClientProtocol(tcp.GetStream());
                p.Connect(new Client(host));
                return p;
            }, ct).ConfigureAwait(false);

            return new BouncyTlsStream(protocol, tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private sealed class Client(string host) : DefaultTlsClient(new BcTlsCrypto())
    {
        public override ProtocolVersion[] GetProtocolVersions() =>
            [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

        public override IDictionary<int, byte[]> GetClientExtensions()
        {
            var extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
            TlsExtensionsUtilities.AddServerNameExtensionClient(extensions,
                [new ServerName(0, Encoding.ASCII.GetBytes(host))]);
            return extensions;
        }

        public override TlsAuthentication GetAuthentication() => new Authentication(host);
    }

    private sealed class Authentication(string host) : ServerOnlyTlsAuthentication
    {
        public override void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate.GetCertificateList();
            if (chain.Length == 0)
                throw new AuthenticationException("Server neposlal certifikát.");

            using var leaf = X509CertificateLoader.LoadCertificate(chain[0].GetEncoded());
            using var x509Chain = new X509Chain();
            var intermediates = new List<X509Certificate2>();
            try
            {
                x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                for (var i = 1; i < chain.Length; i++)
                {
                    var intermediate = X509CertificateLoader.LoadCertificate(chain[i].GetEncoded());
                    intermediates.Add(intermediate);
                    x509Chain.ChainPolicy.ExtraStore.Add(intermediate);
                }

                if (!x509Chain.Build(leaf))
                {
                    var detail = string.Join("; ", x509Chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                    throw new AuthenticationException($"Neplatný certifikát serveru: {detail}");
                }

                var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                    .SelectMany(e => e.EnumerateDnsNames())
                    .ToArray();
                if (names.Length == 0) names = [leaf.GetNameInfo(X509NameType.DnsName, false)];
                if (!names.Any(n => Matches(n, host)))
                    throw new AuthenticationException($"Certifikát neplatí pro hostitele {host}.");
            }
            finally
            {
                foreach (var intermediate in intermediates) intermediate.Dispose();
            }
        }

        private static bool Matches(string pattern, string host)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
                return host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                       && host.Count(c => c == '.') == pattern.Count(c => c == '.');
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Stream, který nad BouncyCastle TLS chová jako běžný síťový stream pro HttpClient.</summary>
internal sealed class BouncyTlsStream(TlsClientProtocol protocol, TcpClient tcp) : Stream
{
    private readonly Stream _inner = protocol.Stream;
    private bool _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        try { return _inner.Read(buffer, offset, count); }
        catch (TlsNoCloseNotifyException) { return 0; }
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        try { return _inner.Read(buffer); }
        catch (TlsNoCloseNotifyException) { return 0; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        try { return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (TlsNoCloseNotifyException) { return 0; }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _inner.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
