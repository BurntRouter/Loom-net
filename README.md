# Loom-net

.NET client library for the Loom service.

- Supports **QUIC** (`System.Net.Quic`) and **HTTP/3** (`HttpClient`)
- Implements Loom wire protocol **v4**
- QUIC ALPN: **`loom/1`**

## Install

- Reference the project, or (if you publish it) `dotnet add package Loom.Net`

## Quick start

### Producer (QUIC)

```csharp
using Loom.Net;

var producer = new LoomProducer(new LoomClientOptions(
    Address: "127.0.0.1:4242",
    Transport: LoomTransport.Quic,
    Tls: new LoomTlsOptions(InsecureSkipVerify: true),
    Name: "producer",
    Room: "default"
));

await producer.ConnectAsync();
await using var payload = File.OpenRead("payload.bin");
await producer.ProduceAsync(key: "my-key"u8.ToArray(), payload: payload, declaredSize: (ulong)payload.Length);
```

### Consumer (QUIC)

```csharp
using Loom.Net;

var consumer = new LoomConsumer(new LoomClientOptions(
    Address: "127.0.0.1:4242",
    Transport: LoomTransport.Quic,
    Tls: new LoomTlsOptions(InsecureSkipVerify: true),
    Name: "consumer",
    Room: "default"
));

await consumer.ConnectAsync();
await consumer.ConsumeLoopAsync(async (key, body, declaredSize, ct) =>
{
    using var ms = new MemoryStream();
    await body.CopyToAsync(ms, ct);
    Console.WriteLine($"key={System.Text.Encoding.UTF8.GetString(key)} bytes={ms.Length}");
}, ct: default);
```

## Notes

- .NET QUIC client keepalive is runtime/version dependent; if you hit idle timeouts, enable keepalive on the **Loom server** (recommended).
- For production, configure real TLS and set `InsecureSkipVerify=false`.

