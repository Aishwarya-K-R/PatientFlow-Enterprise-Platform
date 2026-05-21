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
    ILogger<AIController> logger
) : ControllerBase
{
    private readonly RedisService _redis = redis;
    private readonly LLMService _llm = llm;
    private readonly AISettings _settings = settings.Value;
    private readonly ILogger<AIController> _logger = logger;

    [Authorize(Roles = "ADMIN")]
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest body)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

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
}
