using Microsoft.Extensions.Configuration;
using RagIngestionApp.Models;
using RagIngestionApp.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Configuration
// ─────────────────────────────────────────────────────────────────────────────

IConfiguration configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

string docIntelEndpoint =
    configuration["DocumentIntelligence:Endpoint"]
    ?? throw new InvalidOperationException(
        "DocumentIntelligence:Endpoint is missing from appsettings.json");

string aoaiEndpoint =
    configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException(
        "AzureOpenAI:Endpoint is missing from appsettings.json");

string embeddingDeployment =
    configuration["AzureOpenAI:EmbeddingDeployment"]
    ?? throw new InvalidOperationException(
        "AzureOpenAI:EmbeddingDeployment is missing from appsettings.json");

string chatDeployment =
    configuration["AzureOpenAI:ChatDeployment"]
    ?? throw new InvalidOperationException(
        "AzureOpenAI:ChatDeployment is missing from appsettings.json");

string searchEndpoint =
    configuration["AzureSearch:Endpoint"]
    ?? throw new InvalidOperationException(
        "AzureSearch:Endpoint is missing from appsettings.json");

string indexName =
    configuration["AzureSearch:IndexName"]
    ?? throw new InvalidOperationException(
        "AzureSearch:IndexName is missing from appsettings.json");

// ─────────────────────────────────────────────────────────────────────────────
// Mode selection  (--ingest builds the index; default runs the Q&A assistant)
// ─────────────────────────────────────────────────────────────────────────────

bool ingestMode = args.Contains("--ingest");

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   Azure RAG  ·  Document Q&A Pipeline        ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine($"  Mode           : {(ingestMode ? "INGEST" : "QUERY")}");
Console.WriteLine($"  Search index   : {indexName}");
Console.WriteLine($"  Embedding model: {embeddingDeployment}");
Console.WriteLine($"  Chat model     : {chatDeployment}");
Console.WriteLine($"  Auth           : Entra ID — DefaultAzureCredential");
Console.WriteLine();

if (ingestMode)
    await RunIngestionAsync();
else
    await RunQueryAsync();

// ─────────────────────────────────────────────────────────────────────────────
// INGESTION PIPELINE
// ─────────────────────────────────────────────────────────────────────────────

async Task RunIngestionAsync()
{
    string documentsFolder = Path.Combine(AppContext.BaseDirectory, "Documents");
    string[] pdfFiles      = Directory.GetFiles(documentsFolder, "*.pdf");

    if (pdfFiles.Length == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  No PDF files found in: {documentsFolder}");
        Console.ResetColor();
        return;
    }

    Console.WriteLine($"  Found {pdfFiles.Length} PDF(s) to ingest:");
    foreach (string f in pdfFiles)
        Console.WriteLine($"    • {Path.GetFileName(f)}");
    Console.WriteLine();

    // Step 1 — Create / update search index
    Console.WriteLine("── Step 1/3 · Preparing search index ─────────────────");
    await new SearchIndexService(searchEndpoint, indexName).CreateIndexAsync();
    Console.WriteLine();

    // Step 2 — Extract, chunk, embed
    Console.WriteLine("── Step 2/3 · Extracting and chunking documents ───────");

    var processor        = new DocumentProcessor(docIntelEndpoint);
    var embeddingService = new EmbeddingService(aoaiEndpoint, embeddingDeployment);
    var allDocuments     = new List<SearchDocument>();

    foreach (string pdfPath in pdfFiles)
    {
        string fileName = Path.GetFileName(pdfPath);
        Console.WriteLine($"  Processing: {fileName}");

        List<PageContent>   pages  = await processor.ExtractPagesAsync(pdfPath);
        Console.WriteLine($"    Extracted {pages.Count} page(s)");

        List<DocumentChunk> chunks = TextChunker.Chunk(
            pages, targetSize: 600, overlapSize: 100);
        Console.WriteLine($"    Created   {chunks.Count} chunk(s)");

        for (int i = 0; i < chunks.Count; i++)
        {
            DocumentChunk chunk  = chunks[i];
            Console.Write($"    Embedding chunk {i + 1}/{chunks.Count} ...\r");

            float[] vector = await embeddingService.CreateEmbeddingAsync(chunk.Content);

            allDocuments.Add(new SearchDocument
            {
                Id          = Guid.NewGuid().ToString(),
                SourceFile  = fileName,
                Content     = chunk.Content,
                StartPage   = chunk.StartPage,
                EndPage     = chunk.EndPage,
                ChunkNumber = chunk.ChunkIndex,
                Vector      = vector
            });
        }

        Console.WriteLine($"    Embedded  {chunks.Count} chunk(s) — done          ");
        Console.WriteLine();
    }

    // Step 3 — Upload
    Console.WriteLine("── Step 3/3 · Uploading to Azure AI Search ────────────");
    Console.WriteLine($"  Total documents to upload: {allDocuments.Count}");
    Console.WriteLine();

    await new SearchUploadService(searchEndpoint, indexName)
        .UploadAsync(allDocuments);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(
        $"  Ingestion complete — {allDocuments.Count} chunks from " +
        $"{pdfFiles.Length} document(s) indexed in '{indexName}'.");
    Console.WriteLine("  Run without --ingest to start the Q&A assistant.");
    Console.ResetColor();
}

// ─────────────────────────────────────────────────────────────────────────────
// QUERY PIPELINE
// ─────────────────────────────────────────────────────────────────────────────

async Task RunQueryAsync()
{
    var embeddingService = new EmbeddingService(aoaiEndpoint, embeddingDeployment);

    // Semantic cache — same search service, separate index "rag-answer-cache"
    var cache = new SemanticCacheService(searchEndpoint);
    await cache.EnsureIndexAsync();
    long cacheEntries = await cache.GetEntryCountAsync();

    var ragQuery = new RagQueryService(
        embeddingService, searchEndpoint, indexName, aoaiEndpoint, chatDeployment, cache);

    Console.WriteLine("  Ask questions about your company documents.");
    Console.WriteLine($"  Semantic cache: {cacheEntries} entries (threshold: {SemanticCacheService.SimilarityThreshold:P0})");
    Console.WriteLine("  Type 'exit' to quit.\n");
    Console.WriteLine(new string('─', 48));
    Console.WriteLine();

    int sessionHits = 0, sessionMisses = 0;

    while (true)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("You: ");
        Console.ResetColor();

        string? question = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(question)) continue;
        if (question.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

        try
        {
            Console.WriteLine();

            RagResponse response = await ragQuery.QueryAsync(question);

            if (response.FromCache)
            {
                sessionHits++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚡ CACHE HIT  — ~{response.EstimatedTokensSaved} tokens saved");
                Console.ResetColor();
            }
            else
            {
                sessionMisses++;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  📡 RAG pipeline (answer cached for future users)");
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Assistant: ");
            Console.ResetColor();
            Console.WriteLine(response.Answer);
            Console.WriteLine();

            if (response.Sources.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  ┌─ Sources ──────────────────────────────────────");
                foreach (SourceReference src in response.Sources)
                {
                    string pages = src.StartPage == src.EndPage
                        ? $"p.{src.StartPage}" : $"pp.{src.StartPage}–{src.EndPage}";
                    Console.WriteLine($"  │  {src.SourceFile}  {pages}  (score: {src.Score:F4})");
                }
                Console.WriteLine("  └────────────────────────────────────────────────");
                Console.ResetColor();
            }
            Console.WriteLine();
        }
        catch (Azure.RequestFailedException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  [API Error {ex.Status}] {ex.ErrorCode}: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  [Error] {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\nSession summary — Cache hits: {sessionHits} | Cache misses: {sessionMisses}");
    Console.WriteLine("Goodbye!");
    Console.ResetColor();
}
