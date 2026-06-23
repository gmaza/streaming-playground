using CustomerService.Contracts;
using CustomerService.Customers;
using CustomerService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration -----------------------------------------------------------
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

// --- Domain (trivial) --------------------------------------------------------
builder.Services.AddSingleton<CustomerStore>();

// --- Streaming integration ---------------------------------------------------
// StreamConnection first: it opens the stream system and ensures the stream exists.
builder.Services.AddSingleton<StreamConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamConnection>());

// Publisher second: created after the connection, exposed to endpoints as a singleton.
builder.Services.AddSingleton<CustomerEventPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CustomerEventPublisher>());

var app = builder.Build();

// --- Endpoints ---------------------------------------------------------------
app.MapGet("/customers", (CustomerStore store) => Results.Ok(store.All()));

app.MapGet("/customers/{id}", (string id, CustomerStore store) =>
    store.Find(id) is { } c ? Results.Ok(c) : Results.NotFound());

// The one interesting endpoint: update a customer and publish the change.
app.MapPut("/customers/{id}", async (string id, UpdateCustomerRequest req, CustomerStore store, CustomerEventPublisher publisher) =>
{
    var customer = store.Find(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    // Apply the change and bump the version. We publish a snapshot on EVERY update
    // (even name-only) — deciding whether a change is "interesting" is the
    // consumer's job, which keeps this service ignorant of downstream concerns.
    customer.FullName = req.FullName;
    customer.Email = req.Email;
    customer.PhoneNumber = req.PhoneNumber;
    customer.Version++;

    var @event = new CustomerUpdated(
        EventId: Guid.NewGuid(),
        CustomerId: customer.Id,
        FullName: customer.FullName,
        Email: customer.Email,
        PhoneNumber: customer.PhoneNumber,
        Version: customer.Version,
        OccurredAt: DateTimeOffset.UtcNow);

    await publisher.PublishAsync(@event);

    return Results.Ok(customer);
});

app.Run();

/// <summary>Body for PUT /customers/{id}.</summary>
public sealed record UpdateCustomerRequest(string FullName, string Email, string PhoneNumber);
