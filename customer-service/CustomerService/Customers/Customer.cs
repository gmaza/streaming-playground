namespace CustomerService.Customers;

/// <summary>
/// The customer aggregate this service owns. Trivial on purpose — the focus of the
/// project is the streaming integration, not customer management.
/// </summary>
public sealed class Customer
{
    public required string Id { get; init; }
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string PhoneNumber { get; set; }

    /// <summary>
    /// Bumped on every successful update. Stamped onto each published event so
    /// consumers can deduplicate / order without talking to us.
    /// </summary>
    public long Version { get; set; }
}
