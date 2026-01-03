using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Loom.Net;

internal static class LoomProtocol
{
    internal const string Magic = "LOOM";
    internal const byte Version = 4;

    internal const byte RoleProducer = (byte)'P';
    internal const byte RoleConsumer = (byte)'C';

    internal const ulong FrameAck = 1;

    internal static async ValueTask WriteHelloAsync(Stream s, byte role, string name, string room, string token, CancellationToken ct)
    {
        var bw = new BufferedStream(s, 64 * 1024);
        await bw.WriteAsync(Encoding.ASCII.GetBytes(Magic), ct);
        bw.WriteByte(Version);
        bw.WriteByte(role);
        await WriteUVarintAsync(bw, (ulong)Encoding.UTF8.GetByteCount(name), ct);
        await bw.WriteAsync(Encoding.UTF8.GetBytes(name), ct);
        await WriteUVarintAsync(bw, (ulong)Encoding.UTF8.GetByteCount(room), ct);
        await bw.WriteAsync(Encoding.UTF8.GetBytes(room), ct);
        await WriteUVarintAsync(bw, (ulong)Encoding.UTF8.GetByteCount(token), ct);
        await bw.WriteAsync(Encoding.UTF8.GetBytes(token), ct);
        await bw.FlushAsync(ct);
    }

    internal static async ValueTask WriteMessageHeaderAsync(Stream s, ReadOnlyMemory<byte> key, ulong declaredSize, ulong msgId, CancellationToken ct)
    {
        await WriteUVarintAsync(s, (ulong)key.Length, ct);
        await s.WriteAsync(key, ct);
        await WriteUVarintAsync(s, declaredSize, ct);
        await WriteUVarintAsync(s, msgId, ct);
    }

    internal static async ValueTask<(byte[] key, ulong declaredSize, ulong msgId)> ReadMessageHeaderAsync(Stream s, int maxKeyBytes, CancellationToken ct)
    {
        var keyLen = await ReadUVarintAsync(s, ct);
        if (keyLen == 0 || keyLen > (ulong)maxKeyBytes) throw new InvalidDataException($"bad key len: {keyLen}");
        var key = new byte[keyLen];
        await ReadExactAsync(s, key, ct);
        var declared = await ReadUVarintAsync(s, ct);
        var msgId = await ReadUVarintAsync(s, ct);
        return (key, declared, msgId);
    }

    internal static async ValueTask<(byte[] chunk, bool done)> ReadChunkAsync(Stream s, int maxChunkBytes, CancellationToken ct)
    {
        var n = await ReadUVarintAsync(s, ct);
        if (n == 0) return (Array.Empty<byte>(), true);
        if (n > (ulong)maxChunkBytes) throw new InvalidDataException($"chunk too large: {n}");
        var buf = new byte[n];
        await ReadExactAsync(s, buf, ct);
        return (buf, false);
    }

    internal static async ValueTask WriteChunkAsync(Stream s, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        await WriteUVarintAsync(s, (ulong)chunk.Length, ct);
        if (chunk.Length > 0) await s.WriteAsync(chunk, ct);
    }

    internal static ValueTask WriteEndOfMessageAsync(Stream s, CancellationToken ct) => WriteUVarintAsync(s, 0, ct);

    internal static async ValueTask WriteAckAsync(Stream s, ulong msgId, CancellationToken ct)
    {
        await WriteUVarintAsync(s, FrameAck, ct);
        await WriteUVarintAsync(s, msgId, ct);
        await s.FlushAsync(ct);
    }

    internal static async ValueTask WriteUVarintAsync(Stream s, ulong v, CancellationToken ct)
    {
        var buf = new byte[10];
        var n = 0;
        while (v >= 0x80)
        {
            buf[n++] = (byte)(v | 0x80);
            v >>= 7;
        }
        buf[n++] = (byte)v;
        await s.WriteAsync(buf.AsMemory(0, n), ct);
    }

    internal static async ValueTask<ulong> ReadUVarintAsync(Stream s, CancellationToken ct)
    {
        ulong x = 0;
        var sft = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = await ReadByteAsync(s, ct);
            if (b < 0) throw new EndOfStreamException();
            var bb = (byte)b;
            if ((bb & 0x80) == 0)
            {
                x |= (ulong)bb << sft;
                return x;
            }
            x |= (ulong)(bb & 0x7f) << sft;
            sft += 7;
        }
        throw new InvalidDataException("varint too long");
    }

    private static async ValueTask<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var b = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            var n = await s.ReadAsync(b.AsMemory(0, 1), ct);
            if (n == 0) return -1;
            return b[0];
        }
        finally { ArrayPool<byte>.Shared.Return(b); }
    }

    private static async ValueTask ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
