using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Quic;
using System.Net.Security;

namespace Loom.Net;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class LoomProducer : IAsyncDisposable
{
    private readonly LoomClientOptions _opt;

    private QuicConnection? _qc;
    private QuicStream? _qs;

    private HttpClient? _hc;
    private DuplexHttpContent? _duplex;

    private Stream? _stream;

    public LoomProducer(LoomClientOptions opt) => _opt = opt;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream != null) return;

        if (_opt.Transport == LoomTransport.Http3)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = BuildSslOptions(),
                AutomaticDecompression = DecompressionMethods.None
            };
            _hc = new HttpClient(handler);
            _duplex = new DuplexHttpContent();

            var req = new HttpRequestMessage(HttpMethod.Post, $"https://{_opt.Address}/stream")
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = _duplex
            };

            // Fire the request; response is only read/validated in Finalize.
            _ = _hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _stream = _duplex.WriterStream;
            await LoomProtocol.WriteHelloAsync(_stream, LoomProtocol.RoleProducer, _opt.Name, _opt.Room, _opt.Token, ct);
            return;
        }

        var tls = BuildQuicTlsOptions();
        _qc = await QuicConnection.ConnectAsync(tls, ct);
        _qs = await _qc.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
        _stream = _qs;
        await LoomProtocol.WriteHelloAsync(_stream, LoomProtocol.RoleProducer, _opt.Name, _opt.Room, _opt.Token, ct);
    }

    public async Task ProduceAsync(byte[] key, Stream payload, ulong declaredSize = 0, int chunkSize = 64 * 1024, CancellationToken ct = default)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        await LoomProtocol.WriteMessageHeaderAsync(_stream, key, declaredSize, 0, ct);

        var buf = new byte[chunkSize];
        while (true)
        {
            var n = await payload.ReadAsync(buf, 0, buf.Length, ct);
            if (n == 0) break;
            await LoomProtocol.WriteChunkAsync(_stream, buf.AsMemory(0, n), ct);
        }
        await LoomProtocol.WriteEndOfMessageAsync(_stream, ct);
        await _stream.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_duplex != null)
        {
            _duplex.Complete();
            _hc?.Dispose();
        }
        if (_qs != null) await _qs.DisposeAsync();
    }

    private SslClientAuthenticationOptions BuildSslOptions()
    {
        return new SslClientAuthenticationOptions
        {
            TargetHost = _opt.Tls?.ServerName,
            RemoteCertificateValidationCallback = _opt.Tls?.InsecureSkipVerify == true ? (_, _, _, _) => true : null,
        };
    }

    private QuicClientConnectionOptions BuildQuicTlsOptions()
    {
        var parts = _opt.Address.Split(':');
        return new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(parts[0], int.Parse(parts[1])),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _opt.Tls?.ServerName ?? parts[0],
                ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("loom/1") },
                RemoteCertificateValidationCallback = _opt.Tls?.InsecureSkipVerify == true ? (_, _, _, _) => true : null,
            }
        };
    }
}
