using System.Text;
using System.Text.Json;
using CustomerService.Contracts;
using RabbitMQ.Stream.Client;
using RabbitMQ.Stream.Client.AMQP;
using RabbitMQ.Stream.Client.Reliable;

namespace CustomerService.Messaging;

/// <summary>
/// Publishes <see cref="CustomerUpdated"/> snapshots to the stream.
///
/// Uses the reliable <see cref="Producer"/> (from RabbitMQ.Stream.Client.Reliable)
/// which transparently re-establishes its connection and re-publishes in-flight
/// messages on a broker/network blip — the behaviour you want in production over the
/// raw producer.
///
/// Registered as both a singleton (so endpoints can inject it) and an IHostedService
/// (so the underlying producer is created at startup, after <see cref="StreamConnection"/>,
/// and disposed on shutdown).
/// </summary>
public sealed class CustomerEventPublisher : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly StreamConnection _connection;
    private readonly ILogger<CustomerEventPublisher> _logger;
    private Producer _producer = default!;

    public CustomerEventPublisher(StreamConnection connection, ILogger<CustomerEventPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _producer = await Producer.Create(new ProducerConfig(_connection.System, _connection.StreamName)
        {
            // The confirmation handler runs once the broker has durably stored (or
            // failed to store) a message. This is our publish-confirm signal: we
            // treat anything other than Confirmed as a problem worth logging/alerting.
            ConfirmationHandler = confirmation =>
            {
                if (confirmation.Status != ConfirmationStatus.Confirmed)
                {
                    _logger.LogError(
                        "Publish NOT confirmed: status={Status} publishingId={PublishingId}",
                        confirmation.Status, confirmation.PublishingId);
                }

                return Task.CompletedTask;
            },
        });

        _logger.LogInformation("Customer event publisher ready on stream {Stream}", _connection.StreamName);
    }

    /// <summary>
    /// Serialize and publish a single customer snapshot. Returns once the message is
    /// handed to the producer; durability is reported asynchronously via the
    /// confirmation handler above.
    /// </summary>
    public async Task PublishAsync(CustomerUpdated @event)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(@event, JsonOptions);

        var message = new Message(json)
        {
            Properties = new Properties
            {
                // Helps anyone inspecting the stream in the management UI / tooling.
                ContentType = "application/json",
                MessageId = @event.EventId.ToString(),
            },
        };

        await _producer.Send(message);
        _logger.LogInformation(
            "Published CustomerUpdated customerId={CustomerId} version={Version}",
            @event.CustomerId, @event.Version);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            // Closing flushes outstanding sends and waits for their confirmations.
            await _producer.Close();
        }
    }
}
