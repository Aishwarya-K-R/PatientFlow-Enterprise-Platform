using BillingGrpc;
using Grpc.Net.Client;
using Polly;
using PatientFlow.Common.Metrics;
using PatientFlow.Common.Resilience;

namespace PatientFlow.Patient.Services;

public class BillingGrpcClient
{
    private readonly BillingService.BillingServiceClient _client;
    private readonly ILogger<BillingGrpcClient> _logger;
    private readonly ResiliencePipeline<BillingResponse> _resiliencePipeline;

    public BillingGrpcClient(IConfiguration configuration, ILogger<BillingGrpcClient> logger)
    {
        _logger = logger;

        var address = configuration["BillingService:Address"] ?? "localhost";
        var port = configuration["BillingService:Port"] ?? "5003";

        var grpcUrl = $"http://{address}:{port}";

        _logger.LogInformation("Connecting to Billing Service at {GrpcUrl}", grpcUrl);

        // Cleartext gRPC (h2c) — Billing serves Http1AndHttp2 on the same port.
        // Without this hint, HttpClient negotiates HTTP/1.1 and gRPC fails with
        // HTTP_1_1_REQUIRED (0xd).
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };
        var httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var channel = GrpcChannel.ForAddress(grpcUrl, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        _client = new BillingService.BillingServiceClient(channel);

        // Initialize Polly resilience pipeline (timeout → retry → circuit breaker)
        _resiliencePipeline = ResiliencePolicies.GetCombinedPolicy<BillingResponse>(
            logger, 
            timeout: TimeSpan.FromSeconds(5));
    }

    public async Task<BillingResponse> CreateBillingAccountAsync(int patientId)
    {
        _logger.LogInformation("Creating billing account for Patient {PatientId} with resilience policies", patientId);

        var request = new BillingRequest
        {
            PatientId = patientId
        };

        try
        {
            // Execute gRPC call with Polly resilience pipeline
            var response = await _resiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                return await _client.CreateBillingAccountAsync(request);
            });

            _logger.LogInformation("Billing account created with ID {AccountId} for Patient {PatientId}",
                response.AccountId, patientId);

            return response;
        }
        catch (Exception)
        {
            // Resilience pipeline exhausted (timeouts/retries/circuit-breaker open).
            // Count this as a failure once per call — the Polly retries inside the
            // pipeline are NOT counted individually; this is a final-disposition metric.
            AppMetrics.BillingFailures.Inc();
            throw;
        }
    }
}
