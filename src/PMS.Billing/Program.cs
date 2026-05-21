using Microsoft.EntityFrameworkCore;
using Serilog;
using Prometheus;
using PatientFlow.Billing.Data;
using PatientFlow.Billing.Services;
using PatientFlow.Billing.Grpc;
using PatientFlow.Billing.Kafka;
using PatientFlow.Common.Exceptions;
using PatientFlow.Common.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

builder.Services.AddDbContext<BillingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<BillingAccountService>();
builder.Services.AddSingleton<KafkaProducer>();
builder.Services.AddHostedService<BillingKafkaConsumer>();

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddGrpc();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

app.MapGrpcService<BillingGrpcService>();

app.MapControllers();

app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
