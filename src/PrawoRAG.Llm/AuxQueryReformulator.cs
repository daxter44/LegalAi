using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

/// <summary>
/// <see cref="IQueryReformulator"/> na modelu POMOCNICZYM (Zadanie 11 planu ROU) — ten sam klucz DI
/// co router intencji, świadomie JEDNA zależność, nie dwie.
///
/// Odpala się WYŁĄCZNIE wtedy, gdy pierwsza runda retrievalu nie dała nic wartościowego (bramka
/// abstynencji chce odmówić albo model wypisał frazę odmowy). Dlatego jego koszt płacą tylko pytania,
/// których dzisiejszy wynik jest i tak bezwartościowy — i dlatego 35 s rerankera tego mechanizmu
/// nie blokuje, mimo że blokuje pętlę narzędzia.
/// </summary>
public sealed class AuxQueryReformulator(
    [FromKeyedServices(LlmServiceCollectionExtensions.AuxProviderKey)] ILlmProvider aux)
    : IQueryReformulator
{
    /// <summary>
    /// Prompt celuje w JEDEN udokumentowany tryb awarii: użytkownik pisze potocznie („główny
    /// inspektor danych"), a akt używa innego terminu („Prezes UODO"). To nie jest ogólne
    /// „popraw zapytanie" — model ma podmienić SŁOWNICTWO na ustawowe, zachowując sens.
    /// </summary>
    private const string SystemPrompt =
        """
        Jesteś pomocnikiem wyszukiwania w bazie polskich przepisów i orzeczeń. Poprzednie
        wyszukiwanie nie znalazło pasujących fragmentów.

        Twoim zadaniem jest przełożyć pytanie na TERMINOLOGIĘ USTAWOWĄ — tak, jak nazywa te rzeczy
        polski ustawodawca, a nie jak mówi się o nich potocznie. Przykłady kierunku zmiany:
        „główny inspektor danych" → „Prezes Urzędu Ochrony Danych Osobowych";
        „zwolnienie z pracy na chorobowym" → „rozwiązanie umowy o pracę w okresie niezdolności do pracy";
        „kara za spóźnienie w umowie" → „kara umowna za opóźnienie w wykonaniu zobowiązania".

        Zasady:
        1. Odpowiedz WYŁĄCZNIE samym zapytaniem — bez komentarza, bez cudzysłowów, bez wyjaśnień.
        2. Zachowaj sens pytania. Nie zawężaj go i nie dopisuj faktów, których w nim nie ma.
        3. Użyj słownictwa aktów prawnych i nazw instytucji w brzmieniu urzędowym.
        4. Jeśli nie potrafisz zaproponować sformułowania INNEGO niż wejściowe, odpowiedz: BRAK
        """;

    public async Task<string?> ReformulateAsync(string question, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;

        try
        {
            var request = new LlmRequest
            {
                Messages =
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, question),
                ],
                Temperature = 0,
            };

            var raw = new StringBuilder();
            await foreach (var delta in aux.StreamCompletionAsync(request, ct)) raw.Append(delta);

            return Clean(raw.ToString(), question);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // anulowanie przez użytkownika to nie awaria mechanizmu
        }
        catch
        {
            return null; // brak modelu, timeout, 5xx → dzisiejsza odmowa, bez wyjątku w czacie
        }
    }

    /// <summary>
    /// Odsiewa wyjścia, które nie dadzą INNEGO wyniku retrievalu. Kluczowe: pipeline jest
    /// deterministyczny, więc powtórzenie tego samego zapytania to gwarantowana strata czasu
    /// bez żadnej szansy na inny rezultat.
    /// </summary>
    private static string? Clean(string raw, string original)
    {
        var text = raw.Trim().Trim('"', '\'', '«', '»').Trim();
        if (text.Length == 0) return null;

        // Model gadatliwy: bierzemy pierwszą niepustą linię (prompt każe oddać samo zapytanie).
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return null;
        text = firstLine.Trim('"', '\'').Trim();

        if (text.Equals("BRAK", StringComparison.OrdinalIgnoreCase)) return null;
        if (Equivalent(text, original)) return null;

        return text;
    }

    /// <summary>Równoważne = po zwinięciu białych znaków, wielkości liter i interpunkcji końcowej
    /// to samo zapytanie.</summary>
    private static bool Equivalent(string a, string b)
    {
        static string N(string s) => string.Join(' ',
            s.ToLowerInvariant().Trim(' ', '?', '!', '.', ',')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return N(a) == N(b);
    }
}
