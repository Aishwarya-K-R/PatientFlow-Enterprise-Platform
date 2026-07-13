using Serilog;
using Prometheus;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

// --------------------------------------------------------------------------
// Structured logging (Serilog + Loki) - same shape as every other service so
// the observability dashboards from Phase 6 work out of the box.
// --------------------------------------------------------------------------
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

// --------------------------------------------------------------------------
// MCP server registration.
//
// AddMcpServer() registers the core IMcpServer + all glue services.
// WithHttpTransport() opts into the HTTP + SSE transport (as opposed to the
// stdio transport used by locally-launched MCP servers). This is what makes
// the server reachable from remote clients through the Gateway.
//
// Tools/resources/prompts are attribute-discovered from the assembly; in this
// step we intentionally register nothing so the server starts empty and Step 2
// can add the first tool without touching Program.cs.
// --------------------------------------------------------------------------
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

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

app.MapControllers();

// Prometheus scrape endpoint + per-request HTTP metrics. Kept identical to
// the other services so the existing Grafana dashboards need no changes.
app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

// MCP protocol endpoints. By default this exposes:
//   POST /            - JSON-RPC requests
//   GET  /sse         - Server-Sent Events channel for streamed responses
// Clients like Claude Desktop and GitHub Copilot know how to speak this.
app.MapMcp();

app.Run();
