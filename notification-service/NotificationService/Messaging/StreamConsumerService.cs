using System.Buffers;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationService.Contacts;
using NotificationService.Contracts;
using RabbitMQ.Stream.Client;
using RabbitMQ.Stream.Client.Reliable;

namespace NotificationService.Messaging;

/// <summary>
/// Long-running consumer of the "customer.events" stream.
///
/// Key stream behaviours demonstrated here:
///  * Resume from a server-side stored offset (no external offset store needed).
///  * At-least-once delivery -> the consumer must be idempotent (handled in
///    <see cref="ContactStore"/> via the event Version).
///  * Single active consumer so the work can be scaled to several instances while
///    only one processes at a time (ordered, no double-processing).
///  * Periodic offset checkpointing to bound how much is reprocessed after a crash.
/// </summary>
public sealed class StreamConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqOptions _options;
    private readonly ContactStore _contacts;
    private readonly ILogger<StreamConsumerService> _logger;

    private StreamSystem? _system;
    private Consumer? _consumer;
    private int _sinceLastStore;

    public StreamConsumerService(IOptions<RabbitMqOptions> options, ContactStore contacts, ILogger<StreamConsumerService> logger)
    {
        _options = options.Value;
        _contacts = contacts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _system = await ConnectAsync();
        await EnsureStreamAsync(_system);

        // Where do we start reading? Ask the broker for the last offset we stored
        // under our reference. If there is none (first ever run), start at the very
        // beginning of the stream so we don't miss history.
        IOffsetType offsetSpec;
        try
        {
            var stored = await _system.QueryOffset(_options.ConsumerReference, _options.StreamName);
            // We stored the offset of the last message we fully handled, so resume at
            // the next one to avoid reprocessing it.
            offsetSpec = new OffsetTypeOffset(stored + 1);
            _logger.LogInformation("Resuming from stored offset {Offset}", stored + 1);
        }
        catch (OffsetNotFoundException)
        {
            offsetSpec = new OffsetTypeFirst();
            _logger.LogInformation("No stored offset for reference {Reference} — starting from the first message", _options.ConsumerReference);
        }

        _consumer = await Consumer.Create(new ConsumerConfig(_system, _options.StreamName)
        {
            // Reference ties our consumer to its server-side offset storage.
            Reference = _options.ConsumerReference,
            OffsetSpec = offsetSpec,
            // With several instances running, the broker promotes exactly one to
            // active; the rest stand by and take over on failure.
            IsSingleActiveConsumer = true,
            MessageHandler = (_, consumer, context, message) => HandleAsync(consumer, context, message),
        });

        _logger.LogInformation("Consuming stream {Stream} as {Reference}", _options.StreamName, _options.ConsumerReference);

        // Park until shutdown; delivery happens on the handler callback above.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    private async Task HandleAsync(RawConsumer consumer, MessageContext context, Message message)
    {
        CustomerUpdated? @event;
        try
        {
            // message.Data.Contents is the raw body we published (UTF-8 JSON).
            var body = message.Data.Contents.ToArray();
            @event = JsonSerializer.Deserialize<CustomerUpdated>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            // Poison message: never let it stall the stream. Log and skip (a real
            // system would route it to a dead-letter stream). We still checkpoint so
            // we don't re-read it forever.
            _logger.LogError(ex, "Failed to deserialize message at offset {Offset} — skipping", context.Offset);
            await MaybeStoreOffsetAsync(consumer, context.Offset, force: true);
            return;
        }

        if (@event is null)
        {
            await MaybeStoreOffsetAsync(consumer, context.Offset, force: true);
            return;
        }

        var result = _contacts.Apply(@event);
        switch (result.Outcome)
        {
            case ContactOutcome.FirstSeen:
                _logger.LogInformation("Now tracking contact channels for {CustomerId} (email + phone)", @event.CustomerId);
                break;

            case ContactOutcome.ContactChanged:
                // THE use case: persist updated notification channels.
                _logger.LogInformation(
                    "Notification channels updated for {CustomerId}: emailChanged={EmailChanged} phoneChanged={PhoneChanged} -> email={Email} phone={Phone}",
                    @event.CustomerId, result.EmailChanged, result.PhoneChanged, @event.Email, @event.PhoneNumber);
                break;

            case ContactOutcome.NoContactChange:
                _logger.LogDebug("Ignoring {CustomerId} v{Version}: no email/phone change", @event.CustomerId, @event.Version);
                break;

            case ContactOutcome.Stale:
                _logger.LogDebug("Ignoring stale/duplicate {CustomerId} v{Version}", @event.CustomerId, @event.Version);
                break;
        }

        // Checkpoint our progress. Force a store whenever something actually changed
        // so a crash can't lose a contact update; otherwise batch to save round-trips.
        var changed = result.Outcome is ContactOutcome.ContactChanged or ContactOutcome.FirstSeen;
        await MaybeStoreOffsetAsync(consumer, context.Offset, force: changed);
    }

    private async Task MaybeStoreOffsetAsync(RawConsumer consumer, ulong offset, bool force)
    {
        if (force || ++_sinceLastStore >= _options.StoreOffsetEvery)
        {
            await consumer.StoreOffset(offset);
            _sinceLastStore = 0;
        }
    }

    private async Task<StreamSystem> ConnectAsync()
    {
        var config = new StreamSystemConfig
        {
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            Endpoints = new List<EndPoint> { new DnsEndPoint(_options.Host, _options.Port) },
        };

        _logger.LogInformation("Connecting to RabbitMQ stream system at {Host}:{Port}", _options.Host, _options.Port);
        return await StreamSystem.Create(config);
    }

    private async Task EnsureStreamAsync(StreamSystem system)
    {
        // Idempotent declare so the consumer can start before the publisher.
        if (!await system.StreamExists(_options.StreamName))
        {
            _logger.LogInformation("Stream {Stream} not found — creating it", _options.StreamName);
            await system.CreateStream(new StreamSpec(_options.StreamName)
            {
                MaxAge = _options.MaxAge,
                MaxLengthBytes = (ulong)_options.MaxLengthBytes,
                MaxSegmentSizeBytes = (int)_options.MaxSegmentSizeBytes,
            });
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Graceful shutdown: close the consumer (flushes its final offset store) then
        // the system so the broker releases our resources promptly.
        if (_consumer is not null)
        {
            await _consumer.Close();
        }

        if (_system is not null)
        {
            await _system.Close();
        }
    }
}
