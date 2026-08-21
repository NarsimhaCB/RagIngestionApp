using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using OpenAI.Chat;
using System.Text;

using RagIngestionApp.Models;
using SearchDocument = RagIngestionApp.Models.SearchDocument;

#pragma warning disable OPENAI001

namespace RagIngestionApp.Services;

public record SourceReference(string SourceFile, int StartPage, int EndPage, double Score);

/// <summary>
/// The complete response from a single RAG query.
/// FromCache = true means the answer was returned without calling the AI model.
/// </summary>
public record RagResponse(
    string               Answer,
    List<SourceReference> Sources,
    bool                 FromCache  = false,
    int                  EstimatedTokensSaved = 0);

/// <summary>
/// Full RAG query pipeline with semantic caching.
///
/// On every call:
///   1. Embed the question (small, ~10 tokens).
///   2. Check the semantic cache — if a similar question was answered before,
///      return the cached answer immediately (saves ~800-1500 tokens per query).
///   3. On a cache miss: hybrid search + grounded generation + cache the new answer.
///
/// Authentication: DefaultAzureCredential (Entra ID — no API keys).
/// </summary>
public class RagQueryService
{
    private readonly EmbeddingService      _embeddings;
    private readonly SearchClient          _searchClient;
    private readonly ChatClient            _chatClient;
    private readonly SemanticCacheService  _cache;
    private const    int                   TopK = 5;

    private const string SystemPrompt = """
        You are a knowledgeable HR and policy assistant.
        Answer using ONLY the context passages provided.
        Cite every claim with [Source: <filename>, Page <n>] after the sentence.
        If the context is insufficient, say:
        "I could not find a reliable answer in the available company documents."
        Do not fabricate information or use outside knowledge.
        """;

    public RagQueryService(
        EmbeddingService     embeddingService,
        string               searchEndpoint,
        string               indexName,
        string               aoaiEndpoint,
        string               chatDeployment,
        SemanticCacheService cache)
    {
        _embeddings = embeddingService;
        _cache      = cache;

        _searchClient = new SearchClient(
            new Uri(searchEndpoint), indexName, new DefaultAzureCredential());

        var azureOpenAI = new AzureOpenAIClient(
            new Uri(aoaiEndpoint), new DefaultAzureCredential());

        _chatClient = azureOpenAI.GetChatClient(chatDeployment);
    }

    public async Task<RagResponse> QueryAsync(string question)
    {
        // ── Step 1: Embed the question (always needed — cheap: ~10 tokens) ──────
        float[] questionVector = await _embeddings.CreateEmbeddingAsync(question);

        // ── Step 2: Cache lookup ──────────────────────────────────────────────
        CachedAnswer? cached = await _cache.TryGetAsync(questionVector);
        if (cached is not null)
        {
            // Estimate tokens saved: embedding call is ~10 tokens; the full RAG
            // pipeline (search context ~500 + system prompt ~80 + answer ~300) ≈ 900.
            int saved = EstimateTokens(cached.Answer) + 580;
            return new RagResponse(cached.Answer, [], FromCache: true, EstimatedTokensSaved: saved);
        }

        // ── Step 3: Cache miss — run the full RAG pipeline ───────────────────
        List<SearchResult<SearchDocument>> retrieved =
            await HybridSearchAsync(question, questionVector);

        if (retrieved.Count == 0)
        {
            return new RagResponse(
                "I could not find a reliable answer in the available company documents.", []);
        }

        string contextBlock = BuildContextBlock(retrieved);
        string answer       = await GenerateAnswerAsync(question, contextBlock);

        // ── Step 4: Cache the new answer for future users ────────────────────
        await _cache.SetAsync(question, answer, questionVector);

        List<SourceReference> sources = retrieved
            .Select(r => new SourceReference(
                r.Document.SourceFile, r.Document.StartPage, r.Document.EndPage,
                Math.Round(r.Score ?? 0, 4)))
            .DistinctBy(s => $"{s.SourceFile}|{s.StartPage}|{s.EndPage}")
            .OrderByDescending(s => s.Score)
            .ToList();

        return new RagResponse(answer, sources);
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    private async Task<List<SearchResult<SearchDocument>>> HybridSearchAsync(
        string question, float[] questionVector)
    {
        var vectorQuery = new VectorizedQuery(questionVector.AsMemory())
        {
            KNearestNeighborsCount = TopK
        };
        vectorQuery.Fields.Add("Vector");

        var options = new SearchOptions
        {
            Size = TopK,
            VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } },
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "semantic-config"
            }
        };
        options.Select.Add("Content");
        options.Select.Add("SourceFile");
        options.Select.Add("StartPage");
        options.Select.Add("EndPage");
        options.Select.Add("ChunkNumber");

        var results = await _searchClient.SearchAsync<SearchDocument>(question, options);
        var list    = new List<SearchResult<SearchDocument>>();
        await foreach (var result in results.Value.GetResultsAsync())
            list.Add(result);
        return list;
    }

    private static string BuildContextBlock(List<SearchResult<SearchDocument>> chunks)
    {
        var sb = new StringBuilder();
        int idx = 1;
        foreach (var result in chunks)
        {
            SearchDocument doc   = result.Document;
            string         pages = doc.StartPage == doc.EndPage
                ? $"Page {doc.StartPage}" : $"Pages {doc.StartPage}–{doc.EndPage}";
            sb.AppendLine($"[{idx}] Source: {doc.SourceFile}, {pages}");
            sb.AppendLine(doc.Content);
            sb.AppendLine();
            idx++;
        }
        return sb.ToString().Trim();
    }

    private async Task<string> GenerateAnswerAsync(string question, string contextBlock)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"Context:\n{contextBlock}\n\nQuestion: {question}")
        };
        var response = new StringBuilder();
        await foreach (StreamingChatCompletionUpdate update in
            _chatClient.CompleteChatStreamingAsync(messages))
        {
            foreach (ChatMessageContentPart part in update.ContentUpdate)
                if (!string.IsNullOrEmpty(part.Text))
                    response.Append(part.Text);
        }
        return response.ToString().Trim();
    }

    /// <summary>Rough token estimate: ~4 characters per token (OpenAI average).</summary>
    private static int EstimateTokens(string text) => Math.Max(0, text.Length / 4);
}

#pragma warning restore OPENAI001
