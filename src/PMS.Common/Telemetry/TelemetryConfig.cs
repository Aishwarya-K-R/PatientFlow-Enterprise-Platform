using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace PatientFlow.Common.Telemetry;

public static class TelemetryConfig
{
    public static IServiceCollection AddOpenTelemetryTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];

        // Enable W3C Trace Context propagation globally
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(serviceName))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;

                        // Enrich span with actual HTTP path instead of route template
                        options.EnrichWithHttpRequest = (activity, httpRequest) =>
                        {
                            var path = httpRequest.Path.ToString();
                            activity.DisplayName = $"{httpRequest.Method} {path}";

                            // Also set as attribute for better searchability
                            activity.SetTag("http.target", path);
                        };
                    })
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new RouteTemplateProcessor());

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    builder.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri($"{otlpEndpoint}/v1/traces");
                        options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });
                }
                else
                {
                    builder.AddConsoleExporter();
                }
            });

        return services;
    }
}
