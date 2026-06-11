using Serilog;
using Prometheus;
using PatientFlow.Gateway.Kafka;
using PatientFlow.Common.Telemetry;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

builder.Services.AddOpenTelemetryTracing(builder.Configuration, "Gateway");

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddSingleton<KafkaTopicCreator>();

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

builder.Services.AddHealthChecks();

var app = builder.Build();

// Create Kafka topics on startup
using (var scope = app.Services.CreateScope())
{
    var topicCreator = scope.ServiceProvider.GetRequiredService<KafkaTopicCreator>();
    await topicCreator.CreateTopicsAsync();
}

// Middleware to update span name with actual path (override YARP's route template)
app.Use(async (context, next) =>
{
    var activity = Activity.Current;
    if (activity != null)
    {
        // Update span name to use actual request path instead of route template
        activity.DisplayName = $"{context.Request.Method} {context.Request.Path}";
    }
    await next();
});

app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

app.MapReverseProxy();

app.Run();
