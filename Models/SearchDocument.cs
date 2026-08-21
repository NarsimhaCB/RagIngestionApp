using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace RagIngestionApp.Models;

/// <summary>
/// One text chunk stored in Azure AI Search.
/// Field attributes let FieldBuilder build the index schema automatically.
/// </summary>
public class SearchDocument
{
    [SimpleField(IsKey = true)]
    public string Id { get; set; } = "";

    [SearchableField(AnalyzerName = LexicalAnalyzerName.Values.EnLucene)]
    public string Content { get; set; } = "";

    [SimpleField(IsFilterable = true, IsFacetable = true)]
    public string SourceFile { get; set; } = "";

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public int StartPage { get; set; }

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public int EndPage { get; set; }

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public int ChunkNumber { get; set; }

    [VectorSearchField(
        VectorSearchDimensions   = 1536,
        VectorSearchProfileName  = "vector-profile")]
    public float[] Vector { get; set; } = [];
}
