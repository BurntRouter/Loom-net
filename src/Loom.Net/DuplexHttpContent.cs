using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Channels;

namespace Loom.Net;

internal sealed class DuplexHttpContent : HttpContent
{
    private readonly Channel<byte[]> _ch = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public Stream WriterStream { get; }

    public DuplexHttpContent()
    {
        WriterStream = new ChannelWriteStream(_ch);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await foreach (var buf in _ch.Reader.ReadAllAsync())
        {
            await stream.WriteAsync(buf, 0, buf.Length);
            await stream.FlushAsync();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    public void Complete(Exception? ex = null)
    {
        _ch.Writer.TryComplete(ex);
    }

    private sealed class ChannelWriteStream : Stream
    {
        private readonly Channel<byte[]> _ch;
        public ChannelWriteStream(Channel<byte[]> ch) => _ch = ch;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var b = new byte[count];
            Buffer.BlockCopy(buffer, offset, b, 0, count);
            _ch.Writer.TryWrite(b);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var b = new byte[count];
            Buffer.BlockCopy(buffer, offset, b, 0, count);
            await _ch.Writer.WriteAsync(b, cancellationToken);
        }
    }
}
