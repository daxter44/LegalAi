using PrawoRAG.Ingestion.EurLex;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-UE-1b — klasyfikacja aktu UE, czyli decyzja „czy akt wchodzi do wektorów".
/// Powód istnienia tej decyzji (pomiar 2026-08-26 na populacji 7 756 obowiązujących rozporządzeń
/// i dyrektyw): 4 003 akty (52%) tylko zmieniają inne akty, a 3 662 z nich są już wchłonięte
/// w teksty skonsolidowane, które i tak ingestujemy — ich chunki byłyby duplikatem treści w formie
/// instrukcji zmiany („w załącznikach II, III i IV … wprowadza się zmiany zgodnie z załącznikiem").
/// Tu pilnujemy, żeby ta selekcja nie rozjechała się przy pierwszej refaktoryzacji.
/// </summary>
public class EuActClassifierTests
{
    /// <summary>Realny kształt tytułu aktu czysto nowelizującego: imiesłów „zmieniające" na pozycji
    /// czasownika, a „w sprawie" należy do tytułu aktu ZMIENIANEGO i stoi PO nim.</summary>
    private const string PureAmendmentTitle =
        "Rozporządzenie Komisji (UE) 2020/1 z dnia 1 stycznia 2020 r. zmieniające rozporządzenie (WE) nr 396/2005 w sprawie najwyższych dopuszczalnych poziomów pozostałości";

    [Fact] // Akt bez relacji = własna treść (RODO, AI Act) → pełna ingestia.
    public void Act_without_relations_is_substantive()
    {
        var cls = EuActClassifier.Classify("32016R0679", "Rozporządzenie … w sprawie ochrony osób fizycznych … oraz uchylenia dyrektywy 95/46/WE", relations: null, absorbedBy: null);

        Assert.Equal(EuActClass.Substantive, cls);
        Assert.True(cls.CarriesOwnText());
        Assert.Equal("substantive", cls.ToMetadataValue());
    }

    [Fact] // Nowela wchłonięta w konsolidację aktu bazowego → metadane, ZERO chunków.
    public void Amending_act_absorbed_into_consolidation_carries_no_text()
    {
        var cls = EuActClassifier.Classify(
            "32018R0070",
            "Rozporządzenie Komisji (UE) 2018/70 z dnia 16 stycznia 2018 r. zmieniające załączniki II, III i IV do rozporządzenia (WE) nr 396/2005 … w odniesieniu do najwyższych dopuszczalnych poziomów pozostałości",
            new EuActRelations(["32005R0396"], []),
            absorbedBy: ["02005R0396-20180216"]);

        Assert.Equal(EuActClass.AmendingAbsorbed, cls);
        Assert.False(cls.CarriesOwnText());
        Assert.Equal("amending-absorbed", cls.ToMetadataValue());
    }

    [Fact] // Nowela BEZ konsolidacji wchłaniającej dokłada aktualnej treści → ingestujemy (jak nowele
           // niewchłonięte do tekstu jednolitego w ISAP-ie).
    public void Amending_act_without_consolidation_carries_text()
    {
        var cls = EuActClassifier.Classify("32026R1744", "Rozporządzenie … zmieniające rozporządzenie (UE) 2024/1689 w odniesieniu do zakazanych praktyk", new EuActRelations(["32024R1689"], []), absorbedBy: []);

        Assert.Equal(EuActClass.AmendingOpen, cls);
        Assert.True(cls.CarriesOwnText());
    }

    [Fact] // POPRAWKA PO POMIARZE: relacja „uchyla" NIE może odbierać aktowi treści. RODO uchyla
           // dyrektywę 95/46/WE (widać to w realnej odpowiedzi SPARQL-a — fixture sparql_relations.json),
           // GPSR uchyla starą dyrektywę o bezpieczeństwie produktów, MDR uchyla dyrektywy o wyrobach.
           // Reguła „uchyla → tylko metadane" wyrzuciłaby z wektorów najważniejsze akty w korpusie.
    public void Repealing_predecessor_does_not_remove_text()
    {
        var rodo = EuActClassifier.Classify("32016R0679", "Rozporządzenie … w sprawie ochrony osób fizycznych … oraz uchylenia dyrektywy 95/46/WE", new EuActRelations([], ["31995L0046"]), absorbedBy: null);

        Assert.Equal(EuActClass.Substantive, rodo);
        Assert.True(rodo.CarriesOwnText());
    }

    [Fact] // Akt, którego CAŁA treść to „traci moc", odsiewa się na poziomie chunków (bojlerplate
           // + próg treściowy), nie klasy — decyzję podejmujemy z tekstem w ręku, nie z grafu relacji.
    public void Pure_repealing_act_is_still_classified_as_substantive()
    {
        var cls = EuActClassifier.Classify("32005R0080", "Rozporządzenie Komisji (WE) nr 80/2005 … uchylające rozporządzenie (EWG) nr 1517/77", new EuActRelations([], ["31977R1517"]), absorbedBy: null);

        Assert.Equal(EuActClass.Substantive, cls);
        Assert.True(cls.CarriesOwnText());
    }

    [Fact] // „Zmieniające X i uchylające Y" ma treść ZMIENIAJĄCĄ, więc rozstrzyga wchłonięcie zmian.
    public void Act_that_amends_and_repeals_is_judged_by_amendment()
    {
        var relations = new EuActRelations(["32005R0396"], ["31977R1517"]);

        Assert.Equal(EuActClass.AmendingAbsorbed,
            EuActClassifier.Classify("32020R0001", PureAmendmentTitle, relations, absorbedBy: ["02005R0396-20200101"]));
        Assert.Equal(EuActClass.AmendingOpen,
            EuActClassifier.Classify("32020R0001", PureAmendmentTitle, relations, absorbedBy: []));
    }

    [Fact] // Relacja do samego siebie (spotykana w danych) nie czyni z aktu noweli.
    public void Self_reference_does_not_make_act_amending()
    {
        var cls = EuActClassifier.Classify("32016R0679", "Rozporządzenie … w sprawie ochrony osób fizycznych", new EuActRelations(["32016R0679"], []), absorbedBy: []);

        Assert.Equal(EuActClass.Substantive, cls);
    }

    [Fact] // DRUGA POPRAWKA PO POMIARZE (przebieg bramkowy Fazy 1): akt MERYTORYCZNY też zmienia inne
           // akty w przepisach końcowych, więc „zmienia + wchłonięte" bez sygnału z tytułu wyrzuciło
           // z wektorów AI Act, DSA, DMA, REACH i MDR. Rozstrzyga POZYCJA imiesłowu w tytule.
    public void Substantive_act_amending_others_in_final_provisions_keeps_text()
    {
        // AI Act: własny przedmiot w „w sprawie …", zmiany innych rozporządzeń dopiero na końcu tytułu.
        const string aiActTitle = "Rozporządzenie Parlamentu Europejskiego i Rady (UE) 2024/1689 z dnia 13 czerwca 2024 r. "
            + "w sprawie ustanowienia zharmonizowanych przepisów dotyczących sztucznej inteligencji oraz zmiany rozporządzeń "
            + "(WE) nr 300/2008 … i dyrektyw 2014/90/UE … (akt w sprawie sztucznej inteligencji)";

        var cls = EuActClassifier.Classify(
            "32024R1689", aiActTitle, new EuActRelations(["32008R0300", "32019R2144"], []),
            absorbedBy: ["02019R2144-20260802"]);

        Assert.Equal(EuActClass.Substantive, cls);
        Assert.True(cls.CarriesOwnText());
    }

    [Fact] // TRZECIA POPRAWKA PO POMIARZE: tytuły z CELLAR-a niosą TWARDE SPACJE (U+00A0),
           // więc dopasowanie „w sprawie" nie trafiało i REACH oraz dyrektywa o prawach konsumentów
           // wychodziły jako nowele wchłonięte (czyli bez chunków) w przebiegu bramkowym Fazy 1.
    public void Handles_non_breaking_spaces_in_titles()
    {
        const string nbsp = " "; // twarda spacja — dokładnie tak, jak przychodzi z CELLAR-a
        var reach = $"Rozporządzenie (WE) nr{nbsp}1907/2006 Parlamentu Europejskiego i{nbsp}Rady z{nbsp}dnia 18{nbsp}grudnia 2006 r. "
            + $"w{nbsp}sprawie rejestracji, oceny, udzielania zezwoleń i{nbsp}stosowanych ograniczeń w{nbsp}zakresie chemikaliów (REACH), "
            + "zmieniające dyrektywę 1999/45/WE";

        Assert.False(EuActClassifier.IsPureAmendment(reach));
        Assert.Equal(EuActClass.Substantive,
            EuActClassifier.Classify("32006R1907", reach, new EuActRelations(["31999L0045"], []), absorbedBy: ["01999L0045-20090101"]));
    }

    [Fact] // Rozpoznanie tytułu aktu czysto nowelizującego — kryterium pozycyjne, nie „czy zawiera słowo".
    public void Recognizes_pure_amendment_title_by_position()
    {
        Assert.True(EuActClassifier.IsPureAmendment(PureAmendmentTitle));
        Assert.False(EuActClassifier.IsPureAmendment(
            "Rozporządzenie … w sprawie promowania swobodnego przepływu obywateli … i zmieniające rozporządzenie (UE) nr 1024/2012"));
        Assert.False(EuActClassifier.IsPureAmendment("Rozporządzenie … w sprawie ochrony osób fizycznych"));
        Assert.False(EuActClassifier.IsPureAmendment(null));   // brak tytułu = zakładamy akt merytoryczny
        Assert.False(EuActClassifier.IsPureAmendment("   "));
    }

    [Fact] // Akt zmieniający kilka aktów naraz nadal jest nowelą.
    public void Act_amending_many_acts_is_amending()
    {
        var cls = EuActClassifier.Classify(
            "32013R0519", PureAmendmentTitle, new EuActRelations(["32005R0396", "32008R1272"], []), absorbedBy: []);

        Assert.Equal(EuActClass.AmendingOpen, cls);
    }
}
