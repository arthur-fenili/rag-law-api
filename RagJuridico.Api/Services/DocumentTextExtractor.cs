using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RagJuridico.Api.Services;

public sealed record PageText(int Page, string Text);

/// <summary>Extrai o texto de PDF/TXT/MD, página a página (para citar a página na resposta).</summary>
public static class DocumentTextExtractor
{
    public static readonly string[] SupportedExtensions = [".pdf", ".txt", ".md"];

    public static IReadOnlyList<PageText> Extract(Stream content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => FromPdf(content),
            ".txt" or ".md" => FromText(content),
            _ => throw new NotSupportedException(
                $"Formato '{ext}' não suportado. Use: {string.Join(", ", SupportedExtensions)}.")
        };
    }

    private static List<PageText> FromPdf(Stream content)
    {
        using var document = PdfDocument.Open(content);
        var pages = new List<PageText>();
        foreach (var page in document.GetPages())
        {
            // ContentOrderTextExtractor respeita a ordem de leitura e preserva quebras de linha,
            // bem melhor que page.Text (que costuma colar palavras).
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add(new PageText(page.Number, text));
        }
        return pages;
    }

    private static List<PageText> FromText(Stream content)
    {
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? [] : [new PageText(1, text)];
    }
}
