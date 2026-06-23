namespace NotificationService.Messaging;

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

    public string StreamName { get; init; } = "customer.events";

    /// <summary>
    /// Consumer reference under which the broker stores OUR offset. It is the
    /// identity that makes "resume where I left off" work across restarts, so it
    /// must be stable and unique to this logical consumer.
    /// </summary>
    public string ConsumerReference { get; init; } = "notification-service";

    /// <summary>
    /// Persist the offset to the broker at most once per this many messages (plus on
    /// every meaningful change and on shutdown). Batching avoids a network round-trip
    /// per message; the cost of a larger value is more reprocessing after a crash —
    /// which is safe because the consumer is idempotent.
    /// </summary>
    public int StoreOffsetEvery { get; init; } = 10;

    // Retention — only used if THIS service ends up creating the stream (i.e. it
    // starts before the publisher). Kept in sync with the publisher's settings.
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);
    public long MaxLengthBytes { get; init; } = 2_000_000_000;
    public long MaxSegmentSizeBytes { get; init; } = 100_000_000;
}
