namespace PatientFlow.Contracts.Config;

public class AISettings
{
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public List<string> Rules { get; set; } = new();
    public string NoDataMessage { get; set; } = "No data available";
    public string Endpoint { get; set; } = string.Empty;

    // Embedding-specific configuration - separate from the chat model above so
    // both can be swapped independently. Endpoint defaults to Ollama running
    // as a sibling compose service (`ollama:11434`); model defaults to
    // nomic-embed-text (768 dims, matches the pgvector(768) column in
    // PatientEmbeddings).
    public string EmbeddingEndpoint { get; set; } = "http://ollama:11434/api/embeddings";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    // Startup backfill: on AI service start, walk any patients that don't yet
    // have a stored embedding and generate one. Bounded per run so a huge cold
    // database gets caught up over several restarts rather than pinning Ollama
    // for hours during the first boot.
    public bool EmbeddingBackfillEnabled { get; set; } = true;
    public int EmbeddingBackfillMaxPerRun { get; set; } = 500;

    // Retrieval-Augmented Generation knob: how many of the most semantically
    // similar patients to feed into the LLM prompt for a given question.
    // 5 is a sensible default - big enough to catch the right patient when the
    // top-1 match is noisy, small enough to keep the prompt short and cheap.
    // If you raise this, watch prompt-token count in the LLM logs.
    public int TopKResults { get; set; } = 5;

    // Prompt-injection defence knob (Phase 10 Step 6). Enforced in two places:
    //   1. AskRequestValidator (rejects at HTTP boundary with 400 + message)
    //   2. PromptSanitizer     (belt-and-braces guard right before the prompt
    //                           reaches the LLM, in case validation was
    //                           bypassed or the DTO was populated differently)
    // 1000 chars = ~250 tokens, well below llama3.2's context window and long
    // enough for any legitimate clinical question.
    public int MaxQuestionLength { get; set; } = 1000;
}
