namespace Loom.Net;

public enum LoomTransport
{
    Quic,
    Http3
}

public sealed record LoomTlsOptions(
    bool InsecureSkipVerify = false,
    string? ServerName = null,
    string? CaFile = null,
    string? ClientCertFile = null,
    string? ClientKeyFile = null
);

public sealed record LoomClientOptions(
    string Address,
    LoomTransport Transport = LoomTransport.Quic,
    LoomTlsOptions? Tls = null,
    string Name = "client",
    string Room = "default",
    string Token = ""
);
