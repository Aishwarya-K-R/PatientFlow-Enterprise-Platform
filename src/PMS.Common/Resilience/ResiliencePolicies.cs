using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace PatientFlow.Common.Resilience;

/// <summary>
/// Centralized Polly resilience policies for gRPC and HTTP calls.
/// Provides retry with exponential backoff, circuit breaker, and timeout policies.
/// </summary>
public static class ResiliencePolicies
{
    /// <summary>
    /// Retry policy with exponential backoff for transient failures.
    /// Retries 3 times: 1s, 2s, 4s delays.
    /// </summary>
    public static ResiliencePipeline<T> GetRetryPolicy<T>(ILogger logger)
    {
        return new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true, // Adds randomness to prevent thundering herd
                OnRetry = args =>
                {
                    logger.LogWarning("Retry attempt {Attempt} after {Delay}ms due to: {Exception}",
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? "Unknown error");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Circuit breaker policy to prevent cascading failures.
    /// Opens circuit after 5 consecutive failures, stays open for 30 seconds.
    /// </summary>
    public static ResiliencePipeline<T> GetCircuitBreakerPolicy<T>(ILogger logger)
    {
        return new ResiliencePipelineBuilder<T>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<T>
            {
                FailureRatio = 0.5, // Open circuit if 50% of requests fail
                MinimumThroughput = 5, // Need at least 5 requests to calculate ratio
                BreakDuration = TimeSpan.FromSeconds(30),
                SamplingDuration = TimeSpan.FromSeconds(60), // Time window for failure calculation
                OnOpened = args =>
                {
                    logger.LogError("Circuit breaker OPENED due to {Exception}",
                        args.Outcome.Exception?.Message ?? "High failure rate");
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("Circuit breaker CLOSED - service recovered");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger.LogInformation("Circuit breaker HALF-OPEN - testing service health");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Timeout policy to prevent hanging calls.
    /// </summary>
    public static ResiliencePipeline<T> GetTimeoutPolicy<T>(TimeSpan timeout, ILogger logger)
    {
        return new ResiliencePipelineBuilder<T>()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = timeout,
                OnTimeout = args =>
                {
                    logger.LogError("Operation timed out after {Timeout}s", timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Combined policy: Timeout → Retry → Circuit Breaker.
    /// Use this for production gRPC and HTTP calls.
    /// </summary>
    public static ResiliencePipeline<T> GetCombinedPolicy<T>(ILogger logger, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);

        // Compose individual policies into a pipeline
        var timeoutPipeline = GetTimeoutPolicy<T>(timeout.Value, logger);
        var retryPipeline = GetRetryPolicy<T>(logger);
        var circuitBreakerPipeline = GetCircuitBreakerPolicy<T>(logger);

        // Combine in order: Timeout → Retry → Circuit Breaker
        return new ResiliencePipelineBuilder<T>()
            .AddPipeline(timeoutPipeline)
            .AddPipeline(retryPipeline)
            .AddPipeline(circuitBreakerPipeline)
            .Build();
    }
}
