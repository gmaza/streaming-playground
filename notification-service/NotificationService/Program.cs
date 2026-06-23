using NotificationService.Contacts;
using NotificationService.Messaging;

var builder = Host.CreateApplicationBuilder(args);

// Broker configuration from the "RabbitMq" section.
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

// The local contact store (our "database" of notification channels).
builder.Services.AddSingleton<ContactStore>();

// The stream consumer runs for the life of the process.
builder.Services.AddHostedService<StreamConsumerService>();

var host = builder.Build();
host.Run();
