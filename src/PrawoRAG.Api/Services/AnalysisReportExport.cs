using System.Text;
using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Eksport raportu analizy do Markdownu (AJ-12, krok 1): to, co prawnik zabiera z aplikacji —
/// nagłówek, streszczenie, werdykt per fragment z liniami „narusza / do rozważenia", uzasadnienie
/// i lista źródeł z linkami. Czysta funkcja na <see cref="AnalysisSnapshot"/>, więc działa też
/// w trybie z archiwum (bez treści § — cytujemy wtedy tylko nagłówki). Treść dokumentu użytkownika
/// wchodzi TYLKO gdy sesja żyje (snapshot z DB ma puste Text) i tylko jako krótki cytat fragmentu —
/// eksport to lokalny plik użytkownika, nie zapis po naszej stronie.
/// </summary>
public static class AnalysisReportExport
{
    public const int SnippetChars = 300;

    public static string ToMarkdown(AnalysisSnapshot snap, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("# Analiza dokumentu: ").Append(snap.FileName).Append('\n');
        sb.Append("Polecenie: ").Append(snap.Prompt).Append("  \n");
        sb.Append("Data: ").Append(now.ToString("yyyy-MM-dd HH:mm")).Append("  \n");
        sb.Append("Fragmentów: ").Append(snap.Completed).Append(" z ").Append(snap.Total);
        if (snap.Status == AnalysisStatus.Interrupted) sb.Append(" (analiza przerwana — raport częściowy)");
        sb.Append("\n\n");

        var done = snap.Results.Where(r => r is not null).Select(r => r!).ToList();
        if (done.Count > 0)
            sb.Append("**").Append(AnalysisReport.Headline(
                done.Select(r => new UnitDigest(r.Heading, r.Verdict, r.Answer ?? r.Error ?? "")).ToList())).Append("**\n\n");

        if (!string.IsNullOrWhiteSpace(snap.Summary))
            sb.Append("## Streszczenie\n\n").Append(snap.Summary.Trim()).Append("\n\n");

        sb.Append("## Fragmenty\n\n");
        foreach (var unit in snap.Units)
        {
            var r = snap.Results[unit.Index - 1];
            sb.Append("### ").Append(unit.Heading).Append(" — ")
              .Append(r is null ? "nieprzeanalizowany" : AnalysisPrompts.Label(r.Verdict)).Append('\n');
            if (unit.Text.Length > 0)
                sb.Append("> ").Append(Snippet(unit.Text)).Append("\n\n");
            if (r is null) { sb.Append('\n'); continue; }

            if (r.Violates is not null) sb.Append("- **Narusza:** ").Append(r.Violates).Append('\n');
            if (r.Suggestion is not null) sb.Append("- **Do rozważenia:** ").Append(r.Suggestion).Append('\n');
            if (r.Violates is not null || r.Suggestion is not null) sb.Append('\n');

            if (r.Error is not null) sb.Append("Błąd: ").Append(r.Error).Append("\n\n");
            else if (!string.IsNullOrWhiteSpace(r.Answer)) sb.Append(r.Answer.Trim()).Append("\n\n");

            if (r.Sources.Count > 0)
            {
                sb.Append("Źródła:\n");
                foreach (var s in r.Sources)
                {
                    sb.Append("- [").Append(s.Index).Append("] ").Append(s.Label);
                    if (!string.IsNullOrWhiteSpace(s.Title) && s.Title != s.Label) sb.Append(" — ").Append(s.Title);
                    if (!string.IsNullOrWhiteSpace(s.Url)) sb.Append(" <").Append(s.Url).Append('>');
                    sb.Append('\n');
                }
                sb.Append('\n');
            }
        }

        sb.Append("---\n");
        sb.Append("Raport wygenerowany automatycznie (asystent AI). Werdykty i cytowania wymagają weryfikacji przez prawnika; ")
          .Append("treść dokumentu nie jest przechowywana po stronie usługi.\n");
        return sb.ToString();
    }

    private static string Snippet(string text)
    {
        var clean = string.Join(" ", text.Split(['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= SnippetChars ? clean : clean[..SnippetChars] + "…";
    }
}
