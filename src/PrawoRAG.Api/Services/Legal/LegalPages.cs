using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Markdig;

namespace PrawoRAG.Api.Services.Legal;

/// <summary>
/// Dokumenty prawne (regulamin, polityka prywatności, polityka cookies) renderowane po stronie
/// serwera z plików Markdown wbudowanych w assembly (<c>Legal/*.md</c>, EmbeddedResource).
///
/// Jedno źródło prawdy: ten sam plik czyta prawnik w repozytorium i widzi użytkownik w Serwisie,
/// więc nie ma szansy na rozjazd „wersja u prawnika vs wersja na stronie" (analiza
/// ANALIZA-DOKUMENTY-PRAWNE-2026-09-01.md, R17). Miejsca do uzupełnienia są w treści oznaczone
/// nawiasami kwadratowymi (jak <c>[CENA]</c> na landingu) — <see cref="HasPlaceholders"/> pozwala
/// sprawdzić przed publikacją, czy coś zostało.
///
/// Markdown jest nasz (nie od użytkownika), więc bez sanityzacji HtmlSanitizer; mimo to surowy HTML
/// w Markdownie jest wyłączony, żeby literówka w dokumencie nie stała się znacznikiem.
/// Render odbywa się raz (Lazy) — treść nie zmienia się bez wdrożenia.
/// </summary>
public static class LegalPages
{
    public const string ProductName = "OmniaSI";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().UseAutoLinks().Build();

    public sealed record LegalDocument(string Slug, string Title, string ResourceName);

    public static readonly LegalDocument Regulamin =
        new("regulamin", "Regulamin", "PrawoRAG.Api.Legal.regulamin.md");

    public static readonly LegalDocument Prywatnosc =
        new("prywatnosc", "Polityka prywatności", "PrawoRAG.Api.Legal.polityka-prywatnosci.md");

    public static readonly LegalDocument Cookies =
        new("cookies", "Polityka cookies", "PrawoRAG.Api.Legal.polityka-cookies.md");

    public static readonly IReadOnlyList<LegalDocument> All = [Regulamin, Prywatnosc, Cookies];

    private static readonly Dictionary<string, Lazy<string>> Markdown = All.ToDictionary(
        d => d.Slug, d => new Lazy<string>(() => ReadResource(d.ResourceName)));

    private static readonly Dictionary<string, Lazy<string>> Html = All.ToDictionary(
        d => d.Slug, d => new Lazy<string>(() => Page(d, Markdig.Markdown.ToHtml(Markdown[d.Slug].Value, Pipeline))));

    /// <summary>Surowy Markdown dokumentu (do testów i eksportu).</summary>
    public static string GetMarkdown(LegalDocument doc) => Markdown[doc.Slug].Value;

    /// <summary>Gotowa strona HTML dokumentu (cache na czas życia procesu).</summary>
    public static string GetHtml(LegalDocument doc) => Html[doc.Slug].Value;

    /// <summary>
    /// Czy w treści zostały jeszcze pola do uzupełnienia w nawiasach kwadratowych (np.
    /// <c>[NAZWA PODMIOTU]</c>). Odnośniki Markdown <c>[tekst](url)</c> nie są liczone.
    /// </summary>
    public static bool HasPlaceholders(LegalDocument doc)
        => System.Text.RegularExpressions.Regex.IsMatch(GetMarkdown(doc), @"\[[A-ZĄĆĘŁŃÓŚŹŻ][^\]\n]*\](?!\()");

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Brak zasobu wbudowanego: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Pełny zakres Unicode: polskie znaki w tytule zostają literami, nie encjami (bezpieczeństwo bez zmian —
    // znaki znaczące dla HTML nadal są kodowane).
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string E(string v) => Encoder.Encode(v);

    private static string Page(LegalDocument doc, string bodyHtml)
    {
        var nav = string.Join("", All.Select(d => d.Slug == doc.Slug
            ? $"""<span class="current">{E(d.Title)}</span>"""
            : $"""<a href="/{d.Slug}">{E(d.Title)}</a>"""));

        return $$"""
            <!doctype html>
            <html lang="pl">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(ProductName)}} — {{E(doc.Title)}}</title>
            <link rel="stylesheet" href="/css/tokens.css">
            <style>
              body{font-family:var(--sl-font-base);background:var(--sl-bg);color:var(--sl-text-primary);margin:0;line-height:1.65}
              .top{background:var(--sl-hero-gradient);color:var(--sl-on-dark);padding:var(--s-5) var(--s-6)}
              .top .row{max-width:46rem;margin:0 auto;display:flex;flex-wrap:wrap;gap:var(--s-3) var(--s-6);align-items:center;justify-content:space-between}
              .brand{display:flex;align-items:center;gap:var(--s-2);color:var(--sl-on-dark);text-decoration:none;font-weight:700;
                     font-family:var(--sl-font-display);font-size:var(--fs-20);letter-spacing:-0.01em}
              .brand .mark{width:24px;height:24px;border-radius:var(--sl-radius-md);background:var(--sl-gradient);display:inline-block}
              .docs{display:flex;flex-wrap:wrap;gap:var(--s-4);font-size:var(--fs-14)}
              .docs a{color:var(--sl-on-dark-soft);text-decoration:none}
              .docs a:hover{color:var(--sl-on-dark);text-decoration:underline}
              .docs .current{color:var(--sl-on-dark);font-weight:600}
              main{max-width:46rem;margin:0 auto;padding:var(--s-8) var(--s-6) var(--s-12)}
              article{background:var(--sl-surface);border-radius:var(--sl-radius-xl);box-shadow:var(--sl-shadow-card);padding:var(--s-10) var(--s-10)}
              h1{font-family:var(--sl-font-display);font-size:var(--fs-32);line-height:1.2;letter-spacing:-0.01em;margin:0 0 var(--s-4)}
              h2{font-family:var(--sl-font-display);font-size:var(--fs-20);margin:var(--s-10) 0 var(--s-3);padding-top:var(--s-6);border-top:1px solid var(--sl-border)}
              h1 + p{color:var(--sl-text-secondary);font-size:var(--fs-14)}
              p,li{font-size:var(--fs-15)}
              ol ol{margin-top:var(--s-2)}
              li{margin:var(--s-1) 0}
              a{color:var(--sl-accent)}a:hover{color:var(--sl-accent-hover)}
              .tbl{overflow-x:auto}
              table{border-collapse:collapse;width:100%;font-size:var(--fs-14);margin:var(--s-4) 0}
              th,td{text-align:left;vertical-align:top;padding:var(--s-2) var(--s-3);border-bottom:1px solid var(--sl-border)}
              th{font-size:var(--fs-12);letter-spacing:.06em;text-transform:uppercase;color:var(--sl-text-secondary);font-weight:600}
              code{font-family:var(--sl-font-mono);font-size:.9em;background:var(--sl-bg-secondary);padding:.1em .35em;border-radius:var(--sl-radius-sm)}
              .foot{max-width:46rem;margin:0 auto;padding:0 var(--s-6) var(--s-8);font-size:var(--fs-12);color:var(--sl-text-tertiary)}
              @media (max-width:640px){article{padding:var(--s-6) var(--s-5)}h1{font-size:var(--fs-24)} }
              @media print{.top,.foot{display:none}article{box-shadow:none;padding:0} }
            </style>
            </head>
            <body>
            <header class="top"><div class="row">
              <a class="brand" href="/start"><span class="mark" aria-hidden="true"></span>{{E(ProductName)}}</a>
              <nav class="docs" aria-label="Dokumenty">{{nav}}</nav>
            </div></header>
            <main><article>{{bodyHtml}}</article></main>
            <div class="foot">Dokument można zapisać lub wydrukować z poziomu przeglądarki. <a href="/start">Wróć na stronę główną</a>.</div>
            </body>
            </html>
            """;
    }
}
