using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using System.Text;
using System.Text.Json;

namespace RagIngestionApp.Services;

/// <summary>Text content of a single PDF page, with its page number for citations.</summary>
public record PageContent(int PageNumber, string Text);

/// <summary>
/// Extracts page-level text from PDFs using Azure AI Document Intelligence.
/// Authentication: DefaultAzureCredential (Entra ID — no API keys).
///
/// Implementation note: this class uses the protocol overload of
/// AnalyzeDocumentAsync rather than the typed overload that requires
/// AnalyzeDocumentContent. The protocol overload compiles against every
/// released version of Azure.AI.DocumentIntelligence without namespace issues.
/// The request is the same JSON the REST API accepts:
///   { "base64Source": "<base64-encoded PDF bytes>" }
/// </summary>
public class DocumentProcessor
{
    private readonly DocumentIntelligenceClient _client;

    public DocumentProcessor(string endpoint)
    {
        _client = new DocumentIntelligenceClient(
            new Uri(endpoint),
            new DefaultAzureCredential());
    }

    /// <summary>
    /// Extracts text page-by-page from a local PDF file.
    /// Returns one <see cref="PageContent"/> per page so downstream
    /// chunking can track which page each chunk originates from.
    /// </summary>
    public async Task<List<PageContent>> ExtractPagesAsync(string filePath)
    {
        // ── Build the JSON request body ────────────────────────────────────
        // Document Intelligence REST API accepts:
        //   POST /documentModels/{model}:analyze
        //   Content-Type: application/json
        //   Body: { "base64Source": "<base64-encoded bytes>" }
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        string base64   = Convert.ToBase64String(fileBytes);

        string requestJson = JsonSerializer.Serialize(
            new { base64Source = base64 });

        Azure.Core.RequestContent requestContent =
            Azure.Core.RequestContent.Create(
                BinaryData.FromString(requestJson));

        // ── Call Document Intelligence ─────────────────────────────────────
        // The protocol overload returns Operation<BinaryData> and compiles
        // against all versions of Azure.AI.DocumentIntelligence 1.0.x.
        Operation<BinaryData> operation =
            await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-read",
                requestContent);

        // ── Parse the JSON result ──────────────────────────────────────────
        // The result BinaryData contains the AnalyzeResult JSON from the API.
        // We parse it directly with System.Text.Json to avoid any dependency
        // on SDK-internal deserialization helpers that vary across versions.
        return ParsePages(operation.Value);
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    private static List<PageContent> ParsePages(BinaryData resultData)
    {
        var pages = new List<PageContent>();

        using JsonDocument doc = JsonDocument.Parse(resultData.ToString());
        JsonElement root = doc.RootElement;

        // Document Intelligence long-running operation response is wrapped:
        //   API 2024-11-30  →  { "status": "...", "result":        { "pages": [...] } }
        //   API 2023-07-31  →  { "status": "...", "analyzeResult": { "pages": [...] } }
        //   Direct fallback →  { "pages": [...] }
        //
        // We navigate into whichever wrapper is present before looking for "pages".
        JsonElement dataRoot = root;

        if (root.TryGetProperty("result", out JsonElement resultEl))
            dataRoot = resultEl;
        else if (root.TryGetProperty("analyzeResult", out JsonElement analyzeEl))
            dataRoot = analyzeEl;

        if (!dataRoot.TryGetProperty("pages", out JsonElement pagesArray))
            return pages;   // no pages node — return empty (caller will skip this file)

        foreach (JsonElement pageEl in pagesArray.EnumerateArray())
        {
            int pageNumber = pageEl.TryGetProperty("pageNumber", out JsonElement pn)
                ? pn.GetInt32() : 0;

            var sb = new StringBuilder();

            if (pageEl.TryGetProperty("lines", out JsonElement linesEl))
            {
                foreach (JsonElement line in linesEl.EnumerateArray())
                {
                    if (line.TryGetProperty("content", out JsonElement lineContent))
                        sb.AppendLine(lineContent.GetString() ?? string.Empty);
                }
            }

            string text = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add(new PageContent(pageNumber, text));
        }

        return pages;
    }
}
