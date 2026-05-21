using Serilog;
using Prometheus;
using PatientFlow.Gateway.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

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

app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

app.MapReverseProxy();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
