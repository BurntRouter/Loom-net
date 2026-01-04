using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;

namespace Loom.Net;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class LoomConsumer : IAsyncDisposable
{
    private readonly LoomClientOptions _opt;

    private QuicConnection? _qc;
    private QuicStream? _qs;

    private HttpClient? _hc;
    private DuplexHttpContent? _duplex;
    private HttpResponseMessage? _resp;

    private Stream? _rx;
    private Stream? _tx;

    public LoomConsumer(LoomClientOptions opt) => _opt = opt;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_rx != null) return;

        if (_opt.Transport == LoomTransport.Http3)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = _opt.Tls?.ServerName,
                    RemoteCertificateValidationCallback = _opt.Tls?.InsecureSkipVerify == true ? (_, _, _, _) => true : null,
                },
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
            _resp = await _hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _resp.EnsureSuccessStatusCode();

            _tx = _duplex.WriterStream;
            await LoomProtocol.WriteHelloAsync(_tx, LoomProtocol.RoleConsumer, _opt.Name, _opt.Room, _opt.Token, ct);
            _rx = await _resp.Content.ReadAsStreamAsync(ct);
            return;
        }

        var parts = _opt.Address.Split(':');
        var tls = new QuicClientConnectionOptions
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
        _qc = await QuicConnection.ConnectAsync(tls, ct);
        _qs = await _qc.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
        _rx = _qs;
        _tx = _qs;
        await LoomProtocol.WriteHelloAsync(_tx, LoomProtocol.RoleConsumer, _opt.Name, _opt.Room, _opt.Token, ct);
    }

    public async Task ConsumeLoopAsync(Func<byte[], Stream, ulong, CancellationToken, Task> onMessage, int maxKeyBytes = 256, int maxChunkBytes = 64 * 1024, CancellationToken ct = default)
    {
        var attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                if (_rx == null || _tx == null) throw new InvalidOperationException("not connected");
                attempts = 0;

                while (!ct.IsCancellationRequested)
                {
                    var (key, declared, msgId) = await LoomProtocol.ReadMessageHeaderAsync(_rx, maxKeyBytes, ct);
                    await using var body = new LoomChunkStream(_rx, maxChunkBytes, ct);

                    await onMessage(key, body, declared, ct);
                    await body.DrainToEomAsync();

                    await LoomProtocol.WriteAckAsync(_tx, msgId, ct);
                }
            }
            catch (PlatformNotSupportedException)
            {
                throw;
            }
            catch when (!ct.IsCancellationRequested && _opt.AutoReconnect)
            {
                attempts++;
                if (_opt.MaxReconnectAttempts > 0 && attempts >= _opt.MaxReconnectAttempts) throw;

                await ResetAsync();
                await Task.Delay(_opt.ReconnectDelay ?? TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private sealed class LoomChunkStream : Stream
    {
        private readonly Stream _src;
        private readonly int _maxChunk;
        private readonly CancellationToken _ct;

        private byte[]? _buf;
        private int _off;
        private int _rem;
        private bool _done;

        public LoomChunkStream(Stream src, int maxChunk, CancellationToken ct)
        {
            _src = src;
            _maxChunk = maxChunk;
            _ct = ct;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, _ct).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_done) return 0;

            if (_buf != null && _rem > 0 && _off < _rem)
            {
                var n = Math.Min(count, _rem - _off);
                Buffer.BlockCopy(_buf, _off, buffer, offset, n);
                _off += n;
                if (_off >= _rem) { _off = 0; _rem = 0; }
                return n;
            }

            var nChunk = await LoomProtocol.ReadUVarintAsync(_src, cancellationToken);
            if (nChunk == 0)
            {
                _done = true;
                return 0;
            }
            if (nChunk > (ulong)_maxChunk) throw new InvalidDataException($"chunk too large: {nChunk}");

            var need = (int)nChunk;
            _buf ??= new byte[Math.Max(need, 64 * 1024)];
            if (_buf.Length < need) _buf = new byte[need];

            var offRead = 0;
            while (offRead < need)
            {
                var r = await _src.ReadAsync(_buf.AsMemory(offRead, need - offRead), cancellationToken);
                if (r == 0) throw new EndOfStreamException();
                offRead += r;
            }

            _rem = need;
            _off = 0;
            var toCopy = Math.Min(count, need);
            Buffer.BlockCopy(_buf, 0, buffer, offset, toCopy);
            _off = toCopy;
            if (_off >= _rem) { _off = 0; _rem = 0; }
            return toCopy;
        }

        public async Task DrainToEomAsync()
        {
            var tmp = new byte[64 * 1024];
            while (await ReadAsync(tmp, 0, tmp.Length, _ct) > 0) { }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async ValueTask DisposeAsync() => await ResetAsync();

    private async Task ResetAsync()
    {
        _rx = null;
        _tx = null;

        if (_duplex != null)
        {
            _duplex.Complete();
            _duplex = null;
        }

        _resp?.Dispose();
        _resp = null;

        _hc?.Dispose();
        _hc = null;

        if (_qs != null)
        {
            await _qs.DisposeAsync();
            _qs = null;
        }

        if (_qc != null)
        {
            await _qc.DisposeAsync();
            _qc = null;
        }
    }
}
