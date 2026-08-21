using System.Text;

namespace RagIngestionApp.Services;

/// <summary>A text chunk produced from a source document, with its page range.</summary>
public record DocumentChunk(
    string Content,
    int    StartPage,
    int    EndPage,
    int    ChunkIndex);

/// <summary>
/// Splits page-level document content into overlapping text chunks suitable
/// for embedding and vector storage in Azure AI Search.
///
/// Lines are the atomic splitting unit — no mid-word breaks.
/// Overlap carries tail text from the previous chunk into the next,
/// preventing context loss at boundaries.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Produces overlapping chunks from page-level content.
    /// </summary>
    /// <param name="pages">Pages from <see cref="DocumentProcessor"/>.</param>
    /// <param name="targetSize">Target character count per chunk (default 600).</param>
    /// <param name="overlapSize">Characters of tail to repeat in the next chunk (default 100).</param>
    public static List<DocumentChunk> Chunk(
        List<PageContent> pages,
        int targetSize  = 600,
        int overlapSize = 100)
    {
        // Flatten all pages into (line, pageNumber) tuples.
        var lines = new List<(string Text, int Page)>();
        foreach (var page in pages)
        {
            foreach (string raw in page.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = raw.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    lines.Add((trimmed, page.PageNumber));
            }
        }

        if (lines.Count == 0)
            return [];

        var chunks      = new List<DocumentChunk>();
        var buffer      = new StringBuilder();
        int startPage   = lines[0].Page;
        int endPage     = startPage;
        int index       = 0;
        string overlap  = "";
        bool firstRealLine = true;   // tracks when overlap ends and real content begins

        foreach (var (text, page) in lines)
        {
            if (buffer.Length == 0)
            {
                // Seed buffer with overlap from previous chunk (carries context).
                // We do NOT use the overlap's page as startPage — the overlap is
                // borrowed from the previous chunk; startPage must reflect the first
                // new line so citations point at the right page.
                if (!string.IsNullOrEmpty(overlap))
                    buffer.Append(overlap).Append(' ');

                firstRealLine = true;   // next line appended is the first real one
            }

            // Capture startPage from the first real (non-overlap) line only.
            if (firstRealLine)
            {
                startPage     = page;
                firstRealLine = false;
            }

            buffer.Append(text).Append(' ');
            endPage = page;

            if (buffer.Length >= targetSize)
            {
                string content = buffer.ToString().Trim();
                chunks.Add(new DocumentChunk(content, startPage, endPage, ++index));
                overlap = ExtractOverlap(content, overlapSize);
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            string content = buffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(content))
                chunks.Add(new DocumentChunk(content, startPage, endPage, ++index));
        }

        return chunks;
    }

    private static string ExtractOverlap(string text, int size)
    {
        if (text.Length <= size) return text;
        int start     = text.Length - size;
        int spaceIdx  = text.IndexOf(' ', start);
        return spaceIdx > 0 ? text[spaceIdx..].Trim() : text[start..].Trim();
    }
}
