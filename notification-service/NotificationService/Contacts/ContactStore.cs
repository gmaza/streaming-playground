using System.Collections.Concurrent;
using NotificationService.Contracts;

namespace NotificationService.Contacts;

/// <summary>How an incoming event affected our local contact record.</summary>
public enum ContactOutcome
{
    /// <summary>Event Version is &lt;= what we already applied — duplicate/out-of-order; ignore.</summary>
    Stale,
    /// <summary>First time we've seen this customer; contact info recorded.</summary>
    FirstSeen,
    /// <summary>Email and/or phone changed — the case the notification service cares about.</summary>
    ContactChanged,
    /// <summary>Newer version, but email and phone are unchanged (e.g. name-only edit); ignore.</summary>
    NoContactChange,
}

public readonly record struct ContactResult(ContactOutcome Outcome, bool EmailChanged, bool PhoneChanged);

/// <summary>
/// The notification side's own copy of the contact channels (email / phone) it would
/// use to reach a customer. Stands in for a database. Idempotency lives here: we only
/// apply events whose Version is newer than the last one we recorded, so redelivered
/// or out-of-order messages are harmless.
/// </summary>
public sealed class ContactStore
{
    private sealed record Contact(string Email, string PhoneNumber, long Version);

    private readonly ConcurrentDictionary<string, Contact> _byCustomer = new();

    public ContactResult Apply(CustomerUpdated e)
    {
        // Compute the result and the next stored value atomically per customer.
        var result = new ContactResult(ContactOutcome.NoContactChange, false, false);

        _byCustomer.AddOrUpdate(
            e.CustomerId,
            // Not seen before -> record it as FirstSeen.
            _ =>
            {
                result = new ContactResult(ContactOutcome.FirstSeen, EmailChanged: true, PhoneChanged: true);
                return new Contact(e.Email, e.PhoneNumber, e.Version);
            },
            // Seen before -> apply only if newer, and diff the contact channels.
            (_, existing) =>
            {
                if (e.Version <= existing.Version)
                {
                    result = new ContactResult(ContactOutcome.Stale, false, false);
                    return existing; // keep what we had
                }

                var emailChanged = !string.Equals(existing.Email, e.Email, StringComparison.OrdinalIgnoreCase);
                var phoneChanged = !string.Equals(existing.PhoneNumber, e.PhoneNumber, StringComparison.Ordinal);

                result = (emailChanged || phoneChanged)
                    ? new ContactResult(ContactOutcome.ContactChanged, emailChanged, phoneChanged)
                    : new ContactResult(ContactOutcome.NoContactChange, false, false);

                // Advance the version even on a name-only change so future staleness
                // checks stay correct.
                return new Contact(e.Email, e.PhoneNumber, e.Version);
            });

        return result;
    }
}
