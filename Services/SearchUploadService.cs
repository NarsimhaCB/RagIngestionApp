using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

using RagIngestionApp.Models;

// CS0104 FIX: Azure.Search.Documents.Models also has a class called SearchDocument.
// This alias makes every unqualified 'SearchDocument' in this file resolve to ours.
using SearchDocument = RagIngestionApp.Models.SearchDocument;

namespace RagIngestionApp.Services;

/// <summary>
/// Uploads batches of <see cref="SearchDocument"/> objects to Azure AI Search.
/// Authentication: DefaultAzureCredential (Entra ID — no API keys).
/// </summary>
public class SearchUploadService
{
    private readonly SearchClient _client;
    private const    int          BatchSize = 500;

    public SearchUploadService(string endpoint, string indexName)
    {
        _client = new SearchClient(
            new Uri(endpoint),
            indexName,
            new DefaultAzureCredential());
    }

    /// <summary>
    /// Uploads all documents in batches of <see cref="BatchSize"/>.
    /// Re-throws on failure — a partial upload must not continue silently.
    /// </summary>
    public async Task UploadAsync(List<SearchDocument> documents)
    {
        int uploaded = 0;

        for (int offset = 0; offset < documents.Count; offset += BatchSize)
        {
            List<SearchDocument> batch =
                documents.Skip(offset).Take(BatchSize).ToList();

            try
            {
                IndexDocumentsBatch<SearchDocument> indexBatch =
                    IndexDocumentsBatch.Upload(batch);

                IndexDocumentsResult result =
                    await _client.IndexDocumentsAsync(indexBatch);

                foreach (IndexingResult item in result.Results)
                {
                    if (!item.Succeeded)
                        Console.WriteLine(
                            $"  [WARN] Doc {item.Key} failed: {item.ErrorMessage}");
                }

                uploaded += batch.Count;
                Console.WriteLine(
                    $"  Batch uploaded: {batch.Count} chunks " +
                    $"({uploaded}/{documents.Count} total)");
            }
            catch (Azure.RequestFailedException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    $"  [ERROR] Upload failed — HTTP {ex.Status}: {ex.ErrorCode}");
                Console.WriteLine($"  {ex.Message}");
                Console.ResetColor();
                throw;
            }
        }
    }
}
