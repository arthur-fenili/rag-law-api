using System.Text;
using System.Text.RegularExpressions;

namespace RagJuridico.Api.Services;

public sealed record Chunk(int Page, string Text);

/// <summary>
/// Divide o texto em pedaços (chunks) para indexação.
/// Estratégia: respeita parágrafos; se um parágrafo for grande demais, quebra por frases;
/// em último caso, corta no limite. Chunks não atravessam páginas (citação fica precisa).
/// Consecutivos se sobrepõem um pouco para não perder contexto na fronteira.
/// </summary>
public static partial class TextChunker
{
    public static IEnumerable<Chunk> Split(IEnumerable<PageText> pages, int maxChars, int overlapChars)
    {
        if (overlapChars >= maxChars)
            throw new ArgumentException("ChunkOverlap deve ser menor que ChunkSize.");

        foreach (var page in pages)
        {
            var paragraphs = ParagraphBreak()
                .Split(Normalize(page.Text))
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);

            var current = new StringBuilder();
            var hasNewContent = false;

            foreach (var paragraph in paragraphs)
            {
                foreach (var piece in SplitLong(paragraph, maxChars - overlapChars))
                {
                    if (hasNewContent && current.Length + piece.Length + 2 > maxChars)
                    {
                        yield return new Chunk(page.Page, current.ToString().Trim());
                        current = new StringBuilder(Tail(current.ToString(), overlapChars));
                        hasNewContent = false;
                    }

                    if (current.Length > 0) current.Append("\n\n");
                    current.Append(piece);
                    hasNewContent = true;
                }
            }

            if (hasNewContent)
                yield return new Chunk(page.Page, current.ToString().Trim());
        }
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = MultipleSpaces().Replace(text, " ");
        return text.Trim();
    }

    /// <summary>Quebra um parágrafo maior que o limite em frases; frases gigantes são cortadas.</summary>
    private static IEnumerable<string> SplitLong(string paragraph, int maxChars)
    {
        if (paragraph.Length <= maxChars)
        {
            yield return paragraph;
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var sentence in SentenceBreak().Split(paragraph))
        {
            if (buffer.Length > 0 && buffer.Length + sentence.Length + 1 > maxChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (sentence.Length > maxChars)
            {
                for (var i = 0; i < sentence.Length; i += maxChars)
                    yield return sentence.Substring(i, Math.Min(maxChars, sentence.Length - i));
                continue;
            }

            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(sentence);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    /// <summary>Últimos N caracteres, começando numa fronteira de palavra.</summary>
    private static string Tail(string text, int chars)
    {
        if (chars <= 0 || text.Length <= chars) return chars <= 0 ? "" : text;
        var start = text.Length - chars;
        var space = text.IndexOf(' ', start);
        return space >= 0 ? text[(space + 1)..] : text[start..];
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"(?<=[.;:!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();
}
