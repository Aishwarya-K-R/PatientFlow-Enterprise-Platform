namespace PatientFlow.Mcp.Auth;

/// <summary>
/// Bound from <c>ApiKeys</c> in appsettings.
///
/// The config shape is intentionally <c>key -> agent name</c> (not the reverse)
/// because the *lookup direction* at runtime is key → identity: a request
/// arrives carrying a key, and we need the name for it in O(1). Storing it in
/// the other direction would mean scanning every entry on every request.
/// </summary>
public sealed class ApiKeyAuthOptions
{
    public const string SectionName = "ApiKeys";

    /// <summary>
    /// Map of raw API key -> human-readable agent name
    /// (e.g. "claude-code-key-abc123" -> "ClaudeCode").
    /// In production these keys should come from env vars / a secret store,
    /// not committed appsettings. Development uses appsettings for convenience.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}
