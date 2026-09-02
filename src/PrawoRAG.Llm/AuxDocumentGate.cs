using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

/// <summary>Werdykt bramki załączników: czy wgrany plik to dokument do analizy prawnej, czy inna
/// treść (proza, artykuł, praca szkolna…). <see cref="Reason"/> — jedno zdanie do pokazania
/// użytkownikowi przy odmowie.</summary>
public sealed record DocumentGateDecision(bool IsLegalDocument, string? Reason)
{
    public static DocumentGateDecision Document(string? reason = null) => new(true, reason);
}

/// <summary>Bramka intencji dla „Analizy dokumentów" — szybka klasyfikacja PRZED uruchomieniem
/// kilkunastu wywołań LLM z retrievalem: dokument prawny vs treść, na której analiza nie ma sensu
/// („Pan Tadeusz" jako PDF przechodzi każdy limit rozmiaru, a spaliłby całą analizę z puli).</summary>
public interface IDocumentGate
{
    /// <summary>Klasyfikuje próbkę tekstu załącznika. Fail-open: każda awaria/timeout/nie-JSON
    /// przepuszcza plik — bramka jest oszczędnością, nie zabezpieczeniem twardym.</summary>
    Task<DocumentGateDecision> ClassifyAsync(string sample, CancellationToken ct);
}

/// <summary>
/// <see cref="IDocumentGate"/> na modelu POMOCNICZYM — ten sam wzorzec co <see cref="AuxIntentRouter"/>:
/// krótki prompt, wymuszony JSON, temperatura 0, skończony timeout klienta Aux, a każda awaria
/// degraduje w stronę przepuszczenia pliku (fałszywa odmowa dokumentu jest droższa niż wpuszczenie
/// nietypowego — stąd asymetria w prompcie).
/// </summary>
public sealed class AuxDocumentGate(
    [FromKeyedServices(LlmServiceCollectionExtensions.AuxProviderKey)] ILlmProvider aux) : IDocumentGate
{
    private const string SystemPrompt =
        """
        Jesteś klasyfikatorem załączników w asystencie prawnym. Twoim JEDYNYM zadaniem jest ocenić,
        czy przekazany fragment tekstu pochodzi z DOKUMENTU nadającego się do analizy prawnej
        (umowa, regulamin, statut, ogólne warunki, pismo, decyzja administracyjna, wezwanie,
        uchwała, pełnomocnictwo, oferta handlowa itp.).

        Odpowiedz WYŁĄCZNIE obiektem JSON, bez komentarza, w formacie:
        {"dokument": true|false, "uzasadnienie": "…"}

        Zasady:
        1. "dokument": false TYLKO gdy tekst NA PEWNO nie jest dokumentem: powieść, opowiadanie,
           poezja, artykuł prasowy lub naukowy, praca szkolna, przepis kulinarny, instrukcja obsługi
           sprzętu, luźne notatki bez charakteru prawnego.
        2. W KAŻDYM innym przypadku, a zwłaszcza przy jakiejkolwiek wątpliwości, ustaw
           "dokument": true — odrzucenie prawdziwego dokumentu jest znacznie gorsze niż wpuszczenie
           nietypowego.
        3. "uzasadnienie": jedno krótkie zdanie po polsku (pokazywane użytkownikowi przy odmowie).
        """;

    public async Task<DocumentGateDecision> ClassifyAsync(string sample, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sample)) return DocumentGateDecision.Document("pusta próbka");

        try
        {
            var request = new LlmRequest
            {
                Messages =
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, $"Fragment załącznika do oceny:\n\n{sample}"),
                ],
                Temperature = 0,
            };

            var raw = new StringBuilder();
            await foreach (var delta in aux.StreamCompletionAsync(request, ct)) raw.Append(delta);

            var decision = Parse(raw.ToString());
            LatencyLog.Note("docgate.raw", raw.ToString());
            LatencyLog.Note("docgate.decision",
                $"dokument={decision.IsLegalDocument} uzasadnienie=\"{decision.Reason}\"");
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LatencyLog.Note("docgate.error", $"{ex.GetType().Name}: {ex.Message}");
            return DocumentGateDecision.Document($"awaria bramki ({ex.GetType().Name}) — przepuszczam");
        }
    }

    /// <summary>Pierwszy obiekt JSON z odpowiedzi (model bywa gadatliwy); tylko jawne
    /// <c>"dokument": false</c> odrzuca plik — wszystko inne przepuszcza.</summary>
    private static DocumentGateDecision Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return DocumentGateDecision.Document("bramka nie zwróciła JSON-a");

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dokument", out var isDoc) ||
                isDoc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return DocumentGateDecision.Document("bramka nie orzekła jednoznacznie");

            var reason = root.TryGetProperty("uzasadnienie", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return new DocumentGateDecision(isDoc.GetBoolean(), reason);
        }
        catch (JsonException)
        {
            return DocumentGateDecision.Document("niepoprawny JSON bramki");
        }
    }
}
