namespace CustomerService.Contracts;

/// <summary>
/// Wire contract published to the "customer.events" stream.
///
/// This is a SNAPSHOT event: it carries the full current state of the customer,
/// not a delta. That keeps consumers simple (they can diff against their own last
/// known state) and makes the stream replayable from any offset without needing
/// earlier events to reconstruct state.
///
/// NOTE: this record is intentionally DUPLICATED in NotificationService. The two
/// services are independent deployables; in production this contract would live in
/// a shared, versioned NuGet package. We keep the shapes identical by convention.
/// </summary>
public sealed record CustomerUpdated(
    Guid EventId,
    string CustomerId,
    string FullName,
    string Email,
    string PhoneNumber,
    // Monotonically increasing per customer. Lets consumers reject stale/duplicate
    // deliveries (streams are at-least-once on redelivery) and ignore out-of-order
    // events without coordinating with us.
    long Version,
    DateTimeOffset OccurredAt);
