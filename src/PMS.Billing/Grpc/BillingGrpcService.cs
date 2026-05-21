using BillingGrpc;
using Grpc.Core;
using PatientFlow.Billing.Services;

namespace PatientFlow.Billing.Grpc;

public class BillingGrpcService(
    ILogger<BillingGrpcService> logger, 
    BillingAccountService billingAccountService) : BillingService.BillingServiceBase
{
    private readonly ILogger<BillingGrpcService> _logger = logger;
    private readonly BillingAccountService _billingAccountService = billingAccountService;

    public override async Task<BillingResponse> CreateBillingAccount(BillingRequest request, ServerCallContext context)
    {
        _logger.LogInformation("gRPC: Billing request received for PatientId {PatientId}", request.PatientId);

        try
        {
            var billing = await _billingAccountService.CreateAccountAsync(request.PatientId);

            return new BillingResponse
            {
                AccountId = billing.AccountId,
                Status = billing.Status
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create billing account for PatientId {PatientId}", request.PatientId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
