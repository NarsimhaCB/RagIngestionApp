using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using RagIngestionApp.Models;

namespace RagIngestionApp.Services;

/// <summary>Carries the cached answer and metadata returned on a cache hit.</summary>
public record CachedAnswer(string Answer, string OriginalQuestion, int HitCount, DateTimeOffset CachedAt);

/// <summary>
/// Semantic cache backed by Azure AI Search.
///
/// HOW IT WORKS
/// ─────────────
/// 1. Every successfully answered RAG question is embedded and stored in a separate
///    "rag-answer-cache" search index alongside the generated answer.
/// 2. When a new question arrives, it is embedded and a pure vector search runs
///    against all cached question embeddings (cosine similarity).
/// 3. If the top result scores >= SimilarityThreshold (0.92):
///      → Return the cached answer instantly.
///      → No Document Intelligence, no RAG pipeline, no GPT call.
///      → Tokens saved ≈ full pipeline cost (~800–1500 tokens per query).
/// 4. If below threshold (genuinely different question):
///      → Run the RAG pipeline normally.
///      → Store the new answer for future users.
///
/// MULTI-USER BENEFIT
/// ───────────────────
/// User A: "How many days annual leave do I get?"  → RAG pipeline, answer cached.
/// User B: "What is my annual leave entitlement?"  → similarity ~0.96 → CACHE HIT.
/// User C: "How do I claim sick leave?"            → similarity ~0.61 → cache miss, RAG runs.
///
/// NO NEW AZURE RESOURCES
/// ───────────────────────
/// The cache index is created on the same Azure AI Search service.
/// Authentication: DefaultAzureCredential (Entra ID — no keys).
/// </summary>
public class SemanticCacheService
{
    private readonly SearchClient      _cacheClient;
    private readonly SearchIndexClient _indexClient;
    private readonly string            _indexName;
    private readonly bool              _enabled;

    /// <summary>
    /// Minimum score for a cache hit, measured on Azure AI Search's vector
    /// <c>@search.score</c> scale for a PURE (single) vector query with the cosine metric.
    ///
    /// IMPORTANT — this is NOT the raw cosine similarity. For a cosine-metric vector
    /// query Azure returns a synthetic score:  score = 1 / (1 + cosineDistance),
    /// where cosineDistance = 1 − cosineSimilarity. The score therefore ranges from
    /// 0.333 (opposite vectors) to 1.000 (identical vectors) and is monotonic in
    /// similarity. A threshold of 0.92 on this scale corresponds to a cosine
    /// similarity of roughly 0.91 — i.e. the same question rephrased.
    ///
    /// This threshold is ONLY valid for a pure vector query. It must never be applied
    /// to a hybrid query (search text + vector), because hybrid results are ranked by
    /// Reciprocal Rank Fusion whose scores are tiny (~0.016 for a top match) and not
    /// comparable to this range. See TryGetAsync for how the pure vector query is issued.
    ///
    /// Tune down to 0.88 for more aggressive caching; up to 0.96 to be more conservative.
    /// </summary>
    public const float SimilarityThreshold = 0.92f;

    public SemanticCacheService(
        string searchEndpoint,
        string indexName = "rag-answer-cache",
        bool   enabled   = true)
    {
        _indexName = indexName;
        _enabled   = enabled;

        var credential = new DefaultAzureCredential();
        _cacheClient   = new SearchClient(new Uri(searchEndpoint), indexName, credential);
        _indexClient   = new SearchIndexClient(new Uri(searchEndpoint), credential);
    }

    /// <summary>
    /// Creates the rag-answer-cache index if it does not already exist.
    /// Idempotent — safe to call on every startup.
    /// </summary>
    public async Task EnsureIndexAsync()
    {
        if (!_enabled) return;

        var fields = new FieldBuilder().Build(typeof(CacheEntry));

        var index = new SearchIndex(_indexName)
        {
            Fields = fields,
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration("cache-hnsw")
                    {
                        Parameters = new HnswParameters
                        {
                            M              = 4,
                            EfConstruction = 400,
                            EfSearch       = 500,
                            Metric         = VectorSearchAlgorithmMetric.Cosine
                        }
                    }
                },
                Profiles =
                {
                    new VectorSearchProfile("cache-vector-profile", "cache-hnsw")
                }
            }
        };

        await _indexClient.CreateOrUpdateIndexAsync(index);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [cache] Index ready: {_indexName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Looks for a semantically similar question in the cache.
    /// Returns the cached answer when similarity >= SimilarityThreshold, else null.
    /// </summary>
    public async Task<CachedAnswer?> TryGetAsync(float[] questionVector)
    {
        if (!_enabled) return null;

        try
        {
            var vectorQuery = new VectorizedQuery(questionVector.AsMemory())
            {
                KNearestNeighborsCount = 1
            };
            vectorQuery.Fields.Add("QuestionVector");

            var options = new SearchOptions { Size = 1 };
            options.VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } };
            options.Select.Add("Id");
            options.Select.Add("QuestionText");
            options.Select.Add("Answer");
            options.Select.Add("CachedAt");
            options.Select.Add("HitCount");
            // QuestionVector is selected so the best-effort hit-count merge below can
            // write the full entry back WITHOUT clobbering the stored embedding.
            options.Select.Add("QuestionVector");

            // CRITICAL: pass a NULL search text (not "*"). A pure single vector query
            // returns a cosine-based @search.score in the 0.333–1.0 range, which the
            // SimilarityThreshold is calibrated against. Passing "*" here would make
            // this a HYBRID query ranked by Reciprocal Rank Fusion, whose top score is
            // ~0.016 — far below any sensible threshold — so the cache would NEVER hit.
            var response = await _cacheClient.SearchAsync<CacheEntry>(
                searchText: null, options);

            await foreach (var hit in response.Value.GetResultsAsync())
            {
                double score = hit.Score ?? 0;

                if (score >= SimilarityThreshold)
                {
                    // Snapshot the values we return BEFORE launching the increment,
                    // since IncrementHitCountAsync mutates hit.Document.HitCount.
                    var answer = new CachedAnswer(
                        hit.Document.Answer,
                        hit.Document.QuestionText,
                        hit.Document.HitCount + 1,
                        hit.Document.CachedAt);

                    // Best-effort hit-count increment for cache analytics.
                    // Fire-and-forget: a failure here must never break a cache hit.
                    _ = IncrementHitCountAsync(hit.Document);

                    return answer;
                }
            }
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Cache index doesn't exist yet — first startup before EnsureIndexAsync.
            // Silently return null; the pipeline will run and cache the result.
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [cache] TryGet warning: {ex.Message}");
            Console.ResetColor();
        }

        return null;
    }

    /// <summary>
    /// Increments the stored HitCount for a cached entry.
    /// The full entry (including its embedding, which was selected in TryGetAsync) is
    /// written back, so no field is clobbered. Best-effort only — analytics, never on
    /// the critical path of a cache hit.
    /// </summary>
    private async Task IncrementHitCountAsync(CacheEntry entry)
    {
        try
        {
            entry.HitCount += 1;
            await _cacheClient.MergeOrUploadDocumentsAsync(new[] { entry });
        }
        catch
        {
            // Swallow — hit-count is non-essential telemetry.
        }
    }

    /// <summary>
    /// Stores a new question-answer pair after a successful RAG pipeline run.
    /// Non-blocking for the caller — cache write failures are logged and swallowed.
    /// </summary>
    public async Task SetAsync(string questionText, string answer, float[] questionVector)
    {
        if (!_enabled) return;

        try
        {
            var entry = new CacheEntry
            {
                Id             = Guid.NewGuid().ToString(),
                QuestionText   = questionText,
                Answer         = answer,
                QuestionVector = questionVector,
                CachedAt       = DateTimeOffset.UtcNow,
                HitCount       = 0
            };

            await _cacheClient.IndexDocumentsAsync(
                IndexDocumentsBatch.Upload(new[] { entry }));
        }
        catch (Exception ex)
        {
            // Cache write failure is non-fatal: the answer was already returned to the user.
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [cache] Set warning: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>Returns the total number of entries currently in the cache.</summary>
    public async Task<long> GetEntryCountAsync()
    {
        if (!_enabled) return 0;
        try
        {
            var response = await _cacheClient.SearchAsync<CacheEntry>("*",
                new SearchOptions { Size = 0, IncludeTotalCount = true });
            return response.Value.TotalCount ?? 0;
        }
        catch { return 0; }
    }
}
