using System.Net;
using Microsoft.Extensions.Options;
using RabbitMQ.Stream.Client;

namespace CustomerService.Messaging;

/// <summary>
/// Owns the single <see cref="StreamSystem"/> (the multiplexed TCP connection to the
/// stream protocol) for the whole process and makes sure the target stream exists
/// with our retention policy before anything tries to publish.
///
/// Registered as an <see cref="IHostedService"/> so the connection is established
/// once at startup and disposed cleanly on shutdown. It is registered BEFORE the
/// publisher so the system + stream are ready when the publisher starts.
/// </summary>
public sealed class StreamConnection : IHostedService, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<StreamConnection> _logger;

    public StreamConnection(IOptions<RabbitMqOptions> options, ILogger<StreamConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The shared stream system. Available after <see cref="StartAsync"/>.</summary>
    public StreamSystem System { get; private set; } = default!;

    public string StreamName => _options.StreamName;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = new StreamSystemConfig
        {
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            // DnsEndPoint (not IPEndPoint) so the client resolves the advertised
            // host name the broker hands back after the initial connect.
            Endpoints = new List<EndPoint> { new DnsEndPoint(_options.Host, _options.Port) },
        };

        _logger.LogInformation("Connecting to RabbitMQ stream system at {Host}:{Port}", _options.Host, _options.Port);
        System = await StreamSystem.Create(config);

        // Declare the stream idempotently. We only create it when missing so we never
        // fight an existing stream that was made with different retention settings.
        if (!await System.StreamExists(_options.StreamName))
        {
            _logger.LogInformation("Stream {Stream} not found — creating it", _options.StreamName);
            await System.CreateStream(new StreamSpec(_options.StreamName)
            {
                MaxAge = _options.MaxAge,
                // StreamSpec uses ulong for total length and int for segment size.
                MaxLengthBytes = (ulong)_options.MaxLengthBytes,
                MaxSegmentSizeBytes = (int)_options.MaxSegmentSizeBytes,
            });
        }
        else
        {
            _logger.LogInformation("Stream {Stream} already exists — reusing it", _options.StreamName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (System is not null)
        {
            await System.Close();
        }
    }
}
