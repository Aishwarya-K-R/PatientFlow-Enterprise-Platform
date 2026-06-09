using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;
using PatientFlow.Contracts.Dtos;
using PatientFlow.AI.Services;

namespace PatientFlow.AI.Controllers;

[ApiController]
[Route("ai")]
public class AIController(
    RedisService redis,
    LLMService llm,
    IOptions<AISettings> settings,
    AiCacheWarmupService warmupService,
    ILogger<AIController> logger
) : ControllerBase
{
    private readonly RedisService _redis = redis;
    private readonly LLMService _llm = llm;
    private readonly AISettings _settings = settings.Value;
    private readonly AiCacheWarmupService _warmupService = warmupService;
    private readonly ILogger<AIController> _logger = logger;

    [Authorize(Roles = "ADMIN")]
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest body)
    {
        var request = body.Question;

        // Get patient context from Redis
        var cachedContext = await _redis.GetAllPatientContextsAsync();

        var finalContext = string.Join("\n", cachedContext.Values);

        if (string.IsNullOrWhiteSpace(finalContext))
        {
            _logger.LogWarning("No data available for AI response");
            return Ok(new { answer = _settings.NoDataMessage });
        }

        var rulesText = string.Join("\n- ", _settings.Rules);

        var prompt = $@"
            {_settings.SystemPrompt}

            STRICT RULES:
            - {rulesText}

            DATA:
            {finalContext}

            QUESTION:
            {request}";

        var answer = await _llm.AskAsync(prompt);
        var readableAnswer = answer.Replace("\\n", Environment.NewLine);

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        _logger.LogInformation(
            "AUDIT ai-query | userId={UserId} | question={Question} | answerChars={AnswerChars}",
            userId, request, readableAnswer.Length
        );

        return Content(readableAnswer, "text/plain");
    }

    /// <summary>
    /// Admin endpoint to manually trigger cache warmup.
    /// Useful when Redis is flushed or ops needs to force refresh.
    /// Idempotent - checks _cache_initialized flag.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpPost("admin/warmup")]
    public async Task<IActionResult> WarmupCache()
    {
        _logger.LogInformation("Manual cache warmup triggered by admin");

        try
        {
            await _warmupService.WarmupCacheAsync(HttpContext.RequestAborted);
            return Ok(new { message = "Cache warmup completed successfully" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to warmup cache - Patient Service unreachable");
            return StatusCode(503, new { error = "Patient Service unavailable" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache warmup failed");
            return StatusCode(500, new { error = "Cache warmup failed", details = ex.Message });
        }
    }
}
