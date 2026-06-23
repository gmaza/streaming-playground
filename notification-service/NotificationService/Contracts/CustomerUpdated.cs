namespace NotificationService.Contracts;

/// <summary>
/// Wire contract consumed from the "customer.events" stream.
///
/// DUPLICATED from CustomerService on purpose — the services are independent
/// deployables and only share this JSON shape. In production this would be a shared,
/// versioned "contracts" NuGet package. The field names/types MUST match the
/// publisher's record for JSON (web/camelCase) deserialization to bind correctly.
/// </summary>
public sealed record CustomerUpdated(
    Guid EventId,
    string CustomerId,
    string FullName,
    string Email,
    string PhoneNumber,
    long Version,
    DateTimeOffset OccurredAt);
