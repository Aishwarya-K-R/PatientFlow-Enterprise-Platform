using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Prometheus;
using StackExchange.Redis;
using FluentValidation;
using FluentValidation.AspNetCore;
using PatientFlow.Contracts.Config;
using PatientFlow.AI.Services;
using PatientFlow.Common.Exceptions;
using PatientFlow.Common.Telemetry;
using PatientFlow.Contracts.Validators;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

builder.Services.AddOpenTelemetryTracing(builder.Configuration, "AI-Service");

builder.Services.Configure<AISettings>(
    builder.Configuration.GetSection("AI"));

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var configuration = builder.Configuration.GetConnectionString("RedisConnection");
    return ConnectionMultiplexer.Connect(configuration!);
});

builder.Services.AddSingleton<RedisService>();
builder.Services.AddHttpClient<LLMService>();
builder.Services.AddHttpClient<EmbeddingService>();

// Typed HttpClient for Patient Service (service-to-service communication)
builder.Services.AddHttpClient<PatientServiceClient>(client =>
{
    var baseUrl = builder.Configuration["PatientService:BaseUrl"] ?? "http://patient-service:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);

    // Service-to-service auth via shared internal key (bypasses JWT for trusted callers).
    var internalKey = builder.Configuration["PatientService:InternalApiKey"];
    if (!string.IsNullOrEmpty(internalKey))
    {
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalKey);
    }
});

// PatientEmbeddingStore posts vectors back to the Patient service. It shares
// the same base URL and internal auth key as PatientServiceClient - the two
// clients are kept separate so their responsibilities (read all vs write one)
// stay decoupled and we can swap transports for either independently.
builder.Services.AddHttpClient<PatientEmbeddingStore>(client =>
{
    var baseUrl = builder.Configuration["PatientService:BaseUrl"] ?? "http://patient-service:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);

    var internalKey = builder.Configuration["PatientService:InternalApiKey"];
    if (!string.IsNullOrEmpty(internalKey))
    {
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalKey);
    }
});

// VectorSearchService turns a question into a top-K set of patient ids via
// pgvector cosine search. Same base URL + internal key story as the other
// Patient-service clients so the auth story is uniform.
builder.Services.AddHttpClient<VectorSearchService>(client =>
{
    var baseUrl = builder.Configuration["PatientService:BaseUrl"] ?? "http://patient-service:8080";
    client.BaseAddress = new Uri(baseUrl);
    // A vector search issues one Ollama embedding call + one DB query. Keep
    // the timeout generous enough to cover a warm Ollama call (< 1s) plus
    // pgvector index scan on a large corpus.
    client.Timeout = TimeSpan.FromSeconds(30);

    var internalKey = builder.Configuration["PatientService:InternalApiKey"];
    if (!string.IsNullOrEmpty(internalKey))
    {
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalKey);
    }
});

// Register warmup service (Background + injectable for admin endpoint)
builder.Services.AddSingleton<AiCacheWarmupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiCacheWarmupService>());

// PHI redaction - single source of truth for turning a PatientDto into
// a pseudonymised, embedding-safe string. Stateless => singleton.
builder.Services.AddSingleton<PhiRedactor>();

// Shared handler used by every patient Kafka consumer (created / updated /
// deleted / retry). Scoped so it can capture per-message DbContext / HttpClient
// lifetimes cleanly when the consumer opens a scope per message.
builder.Services.AddScoped<PatientEventHandler>();

// Register Kafka consumers (incremental updates after initial warmup).
// One hosted service per topic so each has its own consumer group and Kafka
// tracks offsets independently. Adding a new event type = add a new consumer.
builder.Services.AddHostedService<PatientEventsConsumer>();          // patient-created
builder.Services.AddHostedService<PatientEventsRetryConsumer>();     // patient-created-retry
builder.Services.AddHostedService<PatientUpdatedConsumer>();         // patient-updated
builder.Services.AddHostedService<PatientDeletedConsumer>();         // patient-deleted
builder.Services.AddHostedService<BillingCreatedConsumer>();         // billing-created

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };
});

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<AskRequestValidator>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

app.Run();
