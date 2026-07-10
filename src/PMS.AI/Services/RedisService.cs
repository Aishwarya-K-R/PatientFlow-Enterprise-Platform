using StackExchange.Redis;

namespace PatientFlow.AI.Services;

public class RedisService(IConnectionMultiplexer redis, IConfiguration config)
{
    private const string ContextKeyPrefix = "patient-context-v2:";
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly string _updatedPatientsKey = config["RedisKeys:UpdatedPatientsKey"]!;

    public async Task MarkUpdatedAsync(int patientId)
    {
        await _db.SetAddAsync(_updatedPatientsKey, patientId);
    }

    public async Task<List<int>> GetUpdatedPatientsAsync()
    {
        var values = await _db.SetMembersAsync(_updatedPatientsKey);
        return values.Select(v => (int)v).ToList();
    }

    public async Task ClearUpdatedPatientsAsync()
    {
        await _db.KeyDeleteAsync(_updatedPatientsKey);
    }

    public async Task SetPatientContextAsync(int patientId, string context)
    {
        await _db.StringSetAsync($"{ContextKeyPrefix}{patientId}", context);
    }

    // Overload for string keys (used by consumer for deduplication)
    public async Task SetPatientContextAsync(string key, string value, TimeSpan? expiry = null)
    {
        await _db.StringSetAsync(key, value, expiry);
    }

    public async Task<string?> GetPatientContextAsync(string key)
    {
        return await _db.StringGetAsync(key);
    }

    public async Task DeletePatientContextAsync(string key)
    {
        await _db.KeyDeleteAsync(key);
    }

    public async Task<Dictionary<int, string>> GetAllPatientContextsAsync()
    {
        var server = _db.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints()[0]);
        var keys = server.Keys(pattern: $"{ContextKeyPrefix}*").ToArray();

        var dict = new Dictionary<int, string>();
        foreach (var key in keys)
        {
            var value = await _db.StringGetAsync(key);
            if (!string.IsNullOrEmpty(value))
            {
                int id = int.Parse(key.ToString().Split(":")[1]);
                dict[id] = value!;
            }
        }
        return dict;
    }

    /// <summary>
    /// Fetch the pseudonymised context strings for a specific set of patient
    /// ids in a single MGET round-trip. Used by the RAG retrieval path where
    /// pgvector has already narrowed the corpus to a top-K subset, so we do
    /// not want a KEYS scan across the whole cache.
    /// Missing entries are silently skipped: an unindexed patient just does
    /// not appear in the returned dictionary. Caller can react (e.g. drop
    /// the id from the prompt, log a cache miss) as it sees fit.
    /// </summary>
    public async Task<Dictionary<int, string>> GetPatientContextsAsync(IEnumerable<int> patientIds)
    {
        var idList = patientIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        // Build the RedisKey[] in the same order as idList so we can zip the
        // results back to their originating id without re-parsing the key.
        var redisKeys = idList
            .Select(id => (RedisKey)$"{ContextKeyPrefix}{id}")
            .ToArray();

        var values = await _db.StringGetAsync(redisKeys);

        var dict = new Dictionary<int, string>(idList.Count);
        for (var i = 0; i < idList.Count; i++)
        {
            if (values[i].HasValue)
            {
                dict[idList[i]] = values[i].ToString();
            }
        }
        return dict;
    }
}
