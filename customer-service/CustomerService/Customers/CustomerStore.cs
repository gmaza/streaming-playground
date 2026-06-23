using System.Collections.Concurrent;

namespace CustomerService.Customers;

/// <summary>
/// In-memory customer store seeded with a few records. Stands in for a database so
/// the example stays self-contained. Thread-safe because the minimal API can handle
/// concurrent requests.
/// </summary>
public sealed class CustomerStore
{
    private readonly ConcurrentDictionary<string, Customer> _customers = new();

    public CustomerStore()
    {
        Seed("C-001", "Ada Lovelace", "ada@example.com", "+1-555-0100");
        Seed("C-002", "Alan Turing", "alan@example.com", "+1-555-0200");
        Seed("C-003", "Grace Hopper", "grace@example.com", "+1-555-0300");
    }

    private void Seed(string id, string name, string email, string phone) =>
        _customers[id] = new Customer { Id = id, FullName = name, Email = email, PhoneNumber = phone, Version = 1 };

    public IReadOnlyCollection<Customer> All() => _customers.Values.ToArray();

    public Customer? Find(string id) => _customers.TryGetValue(id, out var c) ? c : null;
}
