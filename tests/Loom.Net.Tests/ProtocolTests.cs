using System.Text;
using Xunit;

namespace Loom.Net.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task Varint_RoundTrip()
    {
        using var ms = new MemoryStream();
        await Loom.Net.LoomProtocol.WriteUVarintAsync(ms, 300, CancellationToken.None);
        ms.Position = 0;
        var v = await Loom.Net.LoomProtocol.ReadUVarintAsync(ms, CancellationToken.None);
        Assert.Equal((ulong)300, v);
    }

    [Fact]
    public async Task Hello_RoundTrip()
    {
        using var ms = new MemoryStream();
        await Loom.Net.LoomProtocol.WriteHelloAsync(ms, Loom.Net.LoomProtocol.RoleProducer, "p", "r", "t", CancellationToken.None);
        ms.Position = 0;

        // Manual parse the fixed bytes and lengths (smoke test)
        var buf = ms.ToArray();
        Assert.Equal("LOOM", Encoding.ASCII.GetString(buf, 0, 4));
        Assert.Equal(4, buf[4]);
        Assert.Equal((byte)'P', buf[5]);
    }
}
