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
    VectorSearchService vectorSearch,
    IOptions<AISettings> settings,
    AiCacheWarmupService warmupService,
    ILogger<AIController> logger
) : ControllerBase
{
    private readonly RedisService _redis = redis;
    private readonly LLMService _llm = llm;
    private readonly VectorSearchService _vectorSearch = vectorSearch;
    private readonly AISettings _settings = settings.Value;
    private readonly AiCacheWarmupService _warmupService = warmupService;
    private readonly ILogger<AIController> _logger = logger;

    [Authorize(Roles = "ADMIN")]
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest body)
    {
        var request = body.Question;

        // RAG retrieval: embed the question, ask pgvector for the top-K most
        // similar patients, then MGET only those patients' pseudonymised
        // contexts from Redis. Previously we dumped the WHOLE cache into the
        // prompt, which fails to scale past a few hundred patients and dilutes
        // the LLM's attention on the actually-relevant records.
        var matches = await _vectorSearch.FindRelevantPatientsAsync(
            request, topK: _settings.TopKResults, HttpContext.RequestAborted);

        string finalContext;
        List<int> promptPatientIds;

        if (matches.Count > 0)
        {
            var contexts = await _redis.GetPatientContextsAsync(matches.Select(m => m.PatientId));

            // Preserve the ranked order returned by pgvector so the closest
            // match appears first in the prompt (LLMs tend to weight earlier
            // items more heavily).
            var ordered = matches
                .Where(m => contexts.ContainsKey(m.PatientId))
                .Select(m => contexts[m.PatientId])
                .ToList();

            finalContext = string.Join("\n", ordered);
            promptPatientIds = matches
                .Where(m => contexts.ContainsKey(m.PatientId))
                .Select(m => m.PatientId)
                .ToList();
        }
        else
        {
            // Vector search returned nothing (empty embedding table, Ollama down,
            // Patient service unreachable). Fall back to the historical
            // "dump everything" behaviour so a partial outage still produces an
            // answer instead of a hard failure. This is a safety net, not the
            // steady-state path.
            _logger.LogWarning(
                "Vector search returned no matches; falling back to full-cache scan");
            var cachedContext = await _redis.GetAllPatientContextsAsync();
            finalContext = string.Join("\n", cachedContext.Values);
            promptPatientIds = cachedContext.Keys.ToList();
        }

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
            "AUDIT ai-query | userId={UserId} | question={Question} | matchedPatients={PatientCount} | promptIds=[{Ids}] | answerChars={AnswerChars}",
            userId, request, promptPatientIds.Count, string.Join(",", promptPatientIds), readableAnswer.Length
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

    /// <summary>
    /// Admin endpoint to force an embedding backfill pass. Independent of
    /// warmup - only touches pgvector (via the Patient service) and Ollama.
    /// Idempotent by construction: patients that already have a vector row
    /// are excluded from the scan, so repeated calls converge to a no-op.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpPost("admin/backfill-embeddings")]
    public async Task<IActionResult> BackfillEmbeddings()
    {
        _logger.LogInformation("Manual embedding backfill triggered by admin");

        try
        {
            await _warmupService.BackfillMissingEmbeddingsAsync(HttpContext.RequestAborted);
            return Ok(new { message = "Embedding backfill completed. Check logs for per-patient outcomes." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Backfill failed - Patient Service unreachable");
            return StatusCode(503, new { error = "Patient Service unavailable" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding backfill failed");
            return StatusCode(500, new { error = "Embedding backfill failed", details = ex.Message });
        }
    }
}
