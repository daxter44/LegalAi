namespace PrawoRAG.Domain.Retrieval;

/// <summary>Zakres pozycji chunków JEDNEGO dokumentu do dociągnięcia (włącznie z oboma końcami).</summary>
public sealed record NeighbourhoodRange(Guid DocumentId, int FromIndex, int ToIndex);

/// <summary>
/// Plan dociągnięcia SĄSIEDNICH artykułów wokół trafień w akcie (Zadanie 1 planu SAS).
///
/// PRZYPADEK ŹRÓDŁOWY: pytanie o limity wpłat na OKI. Retrieval zadziałał — 8 z 8 źródeł przyszło
/// z właściwej ustawy — ale żadne nie zawierało limitu, bo w ustawie nazywa się on inaczej („próg
/// zwolnienia z podatku"). Pominięcie było SYSTEMATYCZNE, nie losowe: kryterium wyboru (podobieństwo
/// do sformułowania użytkownika) jest ślepe na synonim ustawowy, więc dołożenie kolejnych PODOBNYCH
/// chunków tego nie naprawi.
///
/// MECHANIZM: w tekstach prawnych powiązane przepisy leżą FIZYCZNIE OBOK SIEBIE — definicje, wyjątki,
/// progi i limity stoją przy przepisie, który modyfikują. Rozszerzenie sąsiedztwa omija problem
/// terminologii, nie wiedząc nic o terminologii.
///
/// CZTERY DECYZJE PROJEKTOWE:
///
/// 1. <b>Zero metryki „pewności identyfikacji aktu".</b> Nie identyfikujemy aktu — rozszerzamy to, co
///    retrieval już wybrał. Koncentracja wyników (≥N chunków z jednego dokumentu) JEST tym sygnałem
///    i była obecna w przypadku OKI jako 8/8. Nie ma progu podobieństwa do kalibrowania od zera.
/// 2. <b>Jeden mechanizm dla ustawy i kodeksu.</b> Dla 18-stronicowej ustawy trafienia są rozsiane po
///    całym akcie, więc sąsiedztwo daje w praktyce cały akt. Dla kodeksu cywilnego ten sam kod
///    dociągnie artykuły wokół trafień. Nie ma gałęzi „czy cały akt się mieści" — o rozmiarze
///    decyduje budżet tokenów po stronie wołającego.
/// 3. <b>Tylko AKTY.</b> Orzeczenia mają segment <c>section</c> (długie uzasadnienie), więc ich
///    sąsiedztwo nie ma struktury artykułowej, a pełny tekst wyroku to narracja, nie przepisy.
/// 4. <b>Warunek koncentracji ogranicza zasięg zmiany.</b> Pytania, których źródła są rozproszone po
///    wielu dokumentach, nie kwalifikują się — ich prompt nie zmienia się ani o token.
///
/// Czysta funkcja: cała arytmetyka zakresów testowalna bez bazy. Pobranie chunków należy do
/// wołającego (ma DbContext i budżet tokenów).
/// </summary>
public static class ArticleNeighbourhood
{
    /// <param name="final">Finalna lista chunków (po fuzji, capie i TopK) — to, co model dostałby dziś.</param>
    /// <param name="minChunks">Ile chunków z jednego dokumentu kwalifikuje go do rozszerzenia.</param>
    /// <param name="radius">Ile artykułów w każdą stronę. 0 = mechanizm wyłączony (idiom
    /// <see cref="RetrievalQuery.CitationBridgeArticles"/>).</param>
    /// <returns>
    /// Scalone zakresy pozycji per dokument, posortowane rosnąco — akt ma czytać się w prompcie
    /// LINIOWO, a nie w kolejności podobieństwa. Zakresy obejmują też pozycje trafień; wołający
    /// odsiewa to, co już ma.
    /// </returns>
    public static IReadOnlyList<NeighbourhoodRange> Plan(
        IReadOnlyList<RetrievedChunk> final, int minChunks, int radius)
    {
        if (radius <= 0 || final.Count == 0) return [];

        var result = new List<NeighbourhoodRange>();

        foreach (var doc in final
            .Where(c => c.DocType == DocTypes.Act)
            .GroupBy(c => c.DocumentId)
            .Where(g => g.Count() >= minChunks))
        {
            var positions = doc.Select(c => c.ChunkIndex).Distinct().OrderBy(x => x).ToList();

            // Scalanie: kolejny zakres dokleja się do poprzedniego, gdy przedziały [p−r, p+r]
            // nachodzą na siebie albo się stykają — inaczej pobieralibyśmy ten sam fragment aktu
            // wielokrotnie i dublowali źródła w prompcie.
            var from = Math.Max(0, positions[0] - radius);
            var to = positions[0] + radius;

            for (var i = 1; i < positions.Count; i++)
            {
                var nextFrom = Math.Max(0, positions[i] - radius);
                if (nextFrom <= to + 1)
                {
                    to = positions[i] + radius;   // zakresy stykają się → jeden ciągły blok
                    continue;
                }
                result.Add(new NeighbourhoodRange(doc.Key, from, to));
                (from, to) = (nextFrom, positions[i] + radius);
            }
            result.Add(new NeighbourhoodRange(doc.Key, from, to));
        }

        return result;
    }
}
