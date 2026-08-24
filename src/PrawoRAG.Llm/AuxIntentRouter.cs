using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

/// <summary>
/// <see cref="IIntentRouter"/> na modelu POMOCNICZYM (Zadanie 7 planu ROU) — lekki model 6–11 B,
/// zarejestrowany pod kluczem <see cref="LlmServiceCollectionExtensions.AuxProviderKey"/>.
///
/// Dlaczego nie model odpowiadający: pomiar pokazał ~41 s rozumowania na odpowiedź. Dokładanie
/// takiego wywołania do KAŻDEJ wiadomości, tylko żeby rozstrzygnąć „czy to small-talk", podwoiłoby
/// najdroższą operację w systemie. Router potrzebuje jednej decyzji, nie rozumowania — stąd krótki
/// prompt, wymuszony JSON i niski limit tokenów z <see cref="AuxLlmOptions"/>.
///
/// Cała obsługa błędów sprowadza się do jednej reguły: cokolwiek pójdzie nie tak, idziemy do bazy.
/// </summary>
public sealed class AuxIntentRouter(
    [FromKeyedServices(LlmServiceCollectionExtensions.AuxProviderKey)] ILlmProvider aux) : IIntentRouter
{
    /// <summary>
    /// Prompt jest krótki i ASYMETRYCZNY: model dostaje wprost regułę, że przy jakiejkolwiek
    /// wątpliwości wybiera przepisy. To nie uprzejmość — to jedyny kierunek, w którym pomyłka
    /// modelu jest tania.
    /// </summary>
    private const string SystemPrompt =
        """
        Jesteś klasyfikatorem wiadomości w asystencie prawnym. Twoim JEDYNYM zadaniem jest ocenić,
        czy do odpowiedzi potrzebne są przepisy prawa albo orzeczenia sądów z bazy.

        Odpowiedz WYŁĄCZNIE obiektem JSON, bez komentarza, w formacie:
        {"potrzebne_przepisy": true|false, "zapytanie": "…", "uzasadnienie": "…"}

        Zasady:
        1. "potrzebne_przepisy": false TYLKO dla wiadomości, które NA PEWNO nie są pytaniem prawnym:
           powitania („cześć", „siema"), podziękowania, pytania o sam system („co potrafisz?",
           „kim jesteś?"), pogawędka niezwiązana z prawem.
        2. W KAŻDYM innym przypadku, a zwłaszcza przy jakiejkolwiek wątpliwości, ustaw
           "potrzebne_przepisy": true. Pytanie o sytuację życiową, umowę, pracę, rodzinę, podatki,
           firmę czy urząd JEST pytaniem prawnym, nawet jeśli nie wymienia żadnego przepisu.
        3. "zapytanie": gdy potrzebne_przepisy=true — to samo pytanie, poprawione językowo
           i uzupełnione terminologią prawną. Gdy false — pusty string.
        4. "uzasadnienie": maksymalnie jedno krótkie zdanie.
        """;

    public async Task<RouteDecision> RouteAsync(
        string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            return RouteDecision.Retrieval("puste pytanie — bez zmiany zachowania");

        // Follow-up („a co z § 2?") sam w sobie wygląda jak pogawędka, więc router MUSI widzieć
        // poprzednią turę. Bez tego dopytanie do pytania prawnego trafiłoby na ścieżkę small-talku,
        // czyli w najgorszy z możliwych sposobów: odpowiedź prawna bez źródeł.
        var user = new StringBuilder();
        if (history.Count > 0)
            user.Append("Poprzednie pytanie w tej rozmowie: ")
                .Append(history[^1].Question)
                .Append("\n\n");
        user.Append("Wiadomość do oceny: ").Append(question);

        try
        {
            var request = new LlmRequest
            {
                Messages =
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, user.ToString()),
                ],
                Temperature = 0, // klasyfikacja, nie twórczość — determinizm jest tu zaletą
            };

            var raw = new StringBuilder();
            await foreach (var delta in aux.StreamCompletionAsync(request, ct)) raw.Append(delta);

            var decision = Parse(raw.ToString());
            // Diagnostyka (PRAWORAG_LOG_TIMING): surowa odpowiedź + decyzja NA OBU ścieżkach (true
            // i false), nie tylko false jak NoRetrievalEvent w ChatService. Bez tego "poszło do
            // retrievalu" nie da się z uruchomionej apki odróżnić od "model naprawdę tak ocenił"
            // (2026-08-24 — pierwsze żywe użycie z routerem na Gemini ujawniło tę dziurę).
            LatencyLog.Note("router.raw", raw.ToString());
            LatencyLog.Note("router.decision",
                $"potrzebne_przepisy={decision.PotrzebnePrzepisy} uzasadnienie=\"{decision.Uzasadnienie}\"");
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // anulowanie PRZEZ UŻYTKOWNIKA to nie awaria routera — nie tłumimy
        }
        catch (Exception ex)
        {
            // Timeout klienta pomocniczego (skończony, patrz AuxLlmOptions), brak serwera, 5xx…
            LatencyLog.Note("router.error", $"{ex.GetType().Name}: {ex.Message}");
            return RouteDecision.Retrieval($"awaria routera ({ex.GetType().Name}) — fallback do bazy");
        }
    }

    /// <summary>
    /// Wyciąga decyzję z odpowiedzi modelu. Model bywa gadatliwy (dokleja tekst przed/po JSON-ie),
    /// więc bierzemy pierwszy obiekt JSON z odpowiedzi. Cokolwiek innego ⇒ retrieval.
    /// </summary>
    private static RouteDecision Parse(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            return RouteDecision.Retrieval("router nie zwrócił JSON-a — fallback do bazy");

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;

            // Brak pola albo zły typ ⇒ NIE zgadujemy. Tylko jawne `false` schodzi z toru retrievalu.
            if (!root.TryGetProperty("potrzebne_przepisy", out var needs) ||
                needs.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return RouteDecision.Retrieval("router nie orzekł jednoznacznie — fallback do bazy");

            var reason = root.TryGetProperty("uzasadnienie", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";
            var query = root.TryGetProperty("zapytanie", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() : null;

            return needs.GetBoolean()
                ? new RouteDecision(true, string.IsNullOrWhiteSpace(query) ? null : query, reason)
                : new RouteDecision(false, null, reason);
        }
        catch (JsonException)
        {
            return RouteDecision.Retrieval("router zwrócił niepoprawny JSON — fallback do bazy");
        }
    }
}
