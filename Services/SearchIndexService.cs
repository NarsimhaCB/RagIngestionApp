using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace RagIngestionApp.Services;

/// <summary>
/// Creates or updates the Azure AI Search index for the RAG pipeline.
/// Configured with HNSW vector search and semantic ranking.
/// CreateOrUpdateIndexAsync is idempotent — safe to call on every run.
/// Authentication: DefaultAzureCredential (Entra ID — no API keys).
/// </summary>
public class SearchIndexService
{
    private readonly SearchIndexClient _client;
    private readonly string            _indexName;

    public SearchIndexService(string endpoint, string indexName)
    {
        _indexName = indexName;
        _client    = new SearchIndexClient(
            new Uri(endpoint),
            new DefaultAzureCredential());
    }

    public async Task CreateIndexAsync()
    {
        var fields = new FieldBuilder().Build(typeof(Models.SearchDocument));

        var index = new SearchIndex(_indexName)
        {
            Fields = fields,

            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration("hnsw-config")
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
                    new VectorSearchProfile("vector-profile", "hnsw-config")
                }
            },

            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(
                        "semantic-config",
                        new SemanticPrioritizedFields
                        {
                            // FieldBuilder maps C# property names AS-IS (PascalCase).
                            // These names must exactly match the C# property names in SearchDocument.
                            ContentFields  = { new SemanticField("Content")    },
                            KeywordsFields = { new SemanticField("SourceFile") }
                        })
                }
            }
        };

        await _client.CreateOrUpdateIndexAsync(index);
        Console.WriteLine($"  Index ready: {_indexName}");
    }
}
