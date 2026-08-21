# RagIngestionApp

C# RAG pipeline that ingests enterprise PDF documents, indexes them with hybrid
(vector + keyword) search on Azure AI Search, and answers questions with
grounded, cited responses. Authenticated entirely via **Entra ID**
(`DefaultAzureCredential`) — no API keys anywhere.

A semantic answer cache sits in front of the RAG pipeline: repeated or
rephrased questions are served from a prior answer with **no AI model call at
all**, using pure vector similarity search on Azure AI Search.

---

## What this demonstrates

- Document ingestion via **Azure Document Intelligence** (PDF → structured page text)
- Text chunking with page-aware overlap for citation accuracy
- Embeddings via **text-embedding-3-small**
- **Hybrid search** (keyword + vector + semantic ranking) on Azure AI Search
- Grounded generation: answers are constrained to retrieved context and cite
  `[Source: filename, Page n]`
- A **non-AI semantic cache**: repeated/similar questions are answered from a
  prior response via vector similarity alone — zero tokens, zero model calls

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 8.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Azure CLI | latest | [docs.microsoft.com/cli/azure/install-azure-cli](https://docs.microsoft.com/cli/azure/install-azure-cli) |
| Azure OpenAI resource | — | Azure Portal → AI Foundry |
| Azure AI Search resource | — | Azure Portal → Azure AI Search |
| Azure AI Document Intelligence resource | — | Azure Portal → Document Intelligence |

## One-time Azure setup

### 1. Log in with Azure CLI
```bash
az login
```

### 2. Grant yourself access (Entra ID role assignments)

In the Azure Portal, on each resource → Access Control (IAM) → Add role assignment:

| Resource | Role |
|----------|------|
| Azure OpenAI / AI Foundry | Cognitive Services OpenAI User |
| Azure AI Search | Search Index Data Contributor |
| Azure AI Document Intelligence | Cognitive Services User |

### 3. Configure `appsettings.json`

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource>.services.ai.azure.com",
    "EmbeddingDeployment": "text-embedding-3-small",
    "ChatDeployment": "gpt-4.1-mini"
  },
  "AzureSearch": {
    "Endpoint": "https://<your-search-service>.search.windows.net",
    "IndexName": "company-docs"
  },
  "DocumentIntelligence": {
    "Endpoint": "https://<your-docintel-resource>.cognitiveservices.azure.com/"
  }
}
```

You can override any value with an environment variable instead of editing
the file, e.g. `AzureSearch__Endpoint` (double underscore = config hierarchy
separator).

---

## Running

The app has two modes, selected by a command-line flag:

```bash
dotnet restore
dotnet build

# 1. Ingest — extracts, chunks, embeds, and indexes every PDF in Documents/
dotnet run -- --ingest

# 2. Query — interactive Q&A assistant over the indexed documents (default)
dotnet run
```

Sample documents are provided in `Documents/` (synthetic HR policy PDFs) so
the pipeline can be exercised end-to-end without any real company data.

### Example session

```
You: How many days of annual leave do I get?

📡 RAG pipeline (answer cached for future users)
Assistant: Employees are entitled to 20 days of annual leave per year
[Source: leave_policy.pdf, Page 1].

You: What's my annual leave entitlement?

⚡ CACHE HIT — ~890 tokens saved
Assistant: Employees are entitled to 20 days of annual leave per year
[Source: leave_policy.pdf, Page 1].
```

---

## How the semantic cache works (no AI model involved)

1. Every answered question is embedded and stored — question + answer — in a
   separate `rag-answer-cache` index on the same Azure AI Search service.
2. A new question is embedded and matched against cached question embeddings
   using a **pure vector similarity search** (cosine metric).
3. If the top match scores ≥ 0.92 (Azure AI Search's cosine-based
   `@search.score`, roughly a cosine similarity of ~0.91 — i.e. the same
   question rephrased), the cached answer is returned instantly:
   **no Document Intelligence call, no RAG retrieval, no chat model call.**
4. Below the threshold, the full RAG pipeline runs normally and the new
   answer is cached for future users.

This means the second, third, and later users asking a similar question get
an instant, free answer — the caching decision itself is made entirely by
vector search, not by an LLM.

---

## Project structure

```
RagIngestionApp/
├── Program.cs                       Mode selection (--ingest / query loop)
├── Models/
│   ├── SearchDocument.cs            company-docs index schema
│   └── CacheEntry.cs                rag-answer-cache index schema
├── Services/
│   ├── DocumentProcessor.cs         Document Intelligence PDF extraction
│   ├── TextChunker.cs               Page-aware chunking with overlap
│   ├── EmbeddingService.cs          text-embedding-3-small wrapper
│   ├── SearchIndexService.cs        Creates/updates the company-docs index
│   ├── SearchUploadService.cs       Uploads embedded chunks
│   ├── RagQueryService.cs           Hybrid search + grounded generation + cache
│   └── SemanticCacheService.cs      Non-AI vector-similarity answer cache
├── Documents/                       Sample synthetic HR policy PDFs
├── appsettings.json
└── RagIngestionApp.csproj
```
