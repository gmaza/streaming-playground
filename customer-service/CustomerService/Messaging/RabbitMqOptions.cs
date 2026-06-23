namespace CustomerService.Messaging;

/// <summary>
/// Bound from the "RabbitMq" section of appsettings.json. Centralizes every broker
/// knob so nothing is hardcoded in the integration code.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5552;             // stream protocol port
    public string VirtualHost { get; init; } = "/";
    public string UserName { get; init; } = "app";
    public string Password { get; init; } = "app-pass";

    /// <summary>Stream we publish customer snapshots to.</summary>
    public string StreamName { get; init; } = "customer.events";

    // --- Retention. A stream is an append-only log; without a cap it grows until
    // the disk fills. The broker enforces whichever limit is hit first. ---

    /// <summary>Drop segments older than this. 7 days of history by default.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Total size cap for the stream log (default 2 GB).</summary>
    public long MaxLengthBytes { get; init; } = 2_000_000_000;

    /// <summary>
    /// Segment size. Retention is applied per whole segment, so this bounds how
    /// coarsely old data is truncated (default 100 MB).
    /// </summary>
    public long MaxSegmentSizeBytes { get; init; } = 100_000_000;
}
