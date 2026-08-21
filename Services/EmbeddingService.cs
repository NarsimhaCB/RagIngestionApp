using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Embeddings;

#pragma warning disable OPENAI001

namespace RagIngestionApp.Services;

/// <summary>
/// Generates 1536-dimension text embeddings using Azure OpenAI text-embedding-3-small.
/// Authentication: DefaultAzureCredential (Entra ID — no API keys).
/// </summary>
public class EmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;

    public EmbeddingService(string endpoint, string deployment)
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new DefaultAzureCredential());

        _embeddingClient = azureClient.GetEmbeddingClient(deployment);
    }

    /// <summary>
    /// Returns a 1536-dimension float array for the given text.
    /// </summary>
    /// <exception cref="Azure.RequestFailedException">
    /// Propagated to the caller on API errors (401, 429, 404, etc.).
    /// </exception>
    public async Task<float[]> CreateEmbeddingAsync(string text)
    {
        OpenAIEmbedding embedding =
            await _embeddingClient.GenerateEmbeddingAsync(text);

        return embedding.ToFloats().ToArray();
    }
}

#pragma warning restore OPENAI001
