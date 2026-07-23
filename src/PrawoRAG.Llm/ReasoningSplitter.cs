using System.Text;

namespace PrawoRAG.Llm;

/// <summary>
/// Rozdziela strumień odpowiedzi LLM na WIDOCZNĄ treść i „rozumowanie" (thinking/CoT). Czysty automat
/// stanowy (testowalny bez sieci) — karmiony deltami; zwraca fragment widoczny do wyemitowania teraz,
/// a rozumowanie kumuluje wewnętrznie (<see cref="Reasoning"/>). Obsługuje DWA warianty (jednocześnie):
///
/// 1. **Google AI Studio (Gemini/Gemma OpenAI-compat):** każda delta rozumowania niesie flagę
///    <c>delta.extra_content.google.thought=true</c>; dodatkowo model wplata LITERALNE tagi
///    <c>&lt;thought&gt;…&lt;/thought&gt;</c> na granicach (artefakt) — flaga wpycha w stan rozumowania,
///    a tagi są ZAWSZE odrzucane jako delimitery.
/// 2. **Self-hosted (Ollama/llama.cpp, modele reasoning):** BEZ flagi Google — samo <c>&lt;think&gt;</c>/
///    <c>&lt;thought&gt;</c> … <c>&lt;/…&gt;</c> w treści; automat je wykrywa (także rozcięte między deltami).
///
/// Brak flagi i brak tagów (Claude, Bielik) → pass-through: całość widoczna, rozumowanie puste (zero regresji).
/// </summary>
public sealed class ReasoningSplitter
{
    // (marker, czy WCHODZI w rozumowanie). Kolejność bez znaczenia — szukamy najwcześniejszego wystąpienia.
    private static readonly (string Tag, bool Enter)[] Markers =
    [
        ("<think>", true), ("<thought>", true),
        ("</think>", false), ("</thought>", false),
    ];
    private static readonly int MaxTagLen = Markers.Max(m => m.Tag.Length);

    private readonly StringBuilder _reasoning = new();
    private string _pending = ""; // ogon poprzedniej delty, który MOŻE być początkiem taga (bufor granicy)
    private bool _inReasoning;
    private bool _googleMode; // widzieliśmy flagę google.thought → flaga jest AUTORYTATYWNA (jej brak = koniec myślenia)

    /// <summary>Zebrane rozumowanie (bez tagów). Puste = model nie „myślał".</summary>
    public string Reasoning => _reasoning.ToString();
    public bool HasReasoning => _reasoning.Length > 0;

    /// <summary>Podaj kolejną deltę (treść + flaga google.thought). Zwraca tekst WIDOCZNY do wyemitowania.</summary>
    public string Push(string? content, bool isThoughtFlag)
    {
        // Flaga Google jest autorytatywna: true → rozumowanie; a gdy JUŻ ją widzieliśmy, jej brak
        // na kolejnej delcie oznacza KONIEC myślenia (nie polegamy tylko na literalnym </thought>,
        // który jest artefaktem). Self-hosted (flaga nigdy true) → stan pilotują wyłącznie tagi.
        if (isThoughtFlag) { _inReasoning = true; _googleMode = true; }
        else if (_googleMode && _inReasoning) _inReasoning = false;
        if (string.IsNullOrEmpty(content)) return "";

        var visible = new StringBuilder();
        _pending += content;

        while (_pending.Length > 0)
        {
            var (idx, tag, enter) = FindEarliestMarker(_pending);
            if (idx < 0)
            {
                // Brak pełnego markera. Zatrzymaj w buforze tylko ogon, który może być PREFIKSEM markera
                // (żeby nie przeciąć taga rozłożonego na dwie delty); resztę wyemituj do właściwego ujścia.
                var keep = LongestMarkerPrefixSuffix(_pending);
                var flush = _pending[..(_pending.Length - keep)];
                Route(flush, visible);
                _pending = _pending[(_pending.Length - keep)..];
                break;
            }
            Route(_pending[..idx], visible);   // tekst przed tagiem → bieżące ujście
            _inReasoning = enter;               // tag przełącza stan…
            _pending = _pending[(idx + tag.Length)..]; // …i jest ODRZUCany (delimiter/artefakt)
        }
        return visible.ToString();
    }

    /// <summary>Domknięcie strumienia — dopisuje resztę bufora do bieżącego ujścia. Zwraca ostatni
    /// widoczny fragment (zwykle pusty).</summary>
    public string Finish()
    {
        var visible = new StringBuilder();
        if (_pending.Length > 0) { Route(_pending, visible); _pending = ""; }
        return visible.ToString();
    }

    private void Route(string text, StringBuilder visible)
    {
        if (text.Length == 0) return;
        if (_inReasoning) _reasoning.Append(text);
        else visible.Append(text);
    }

    private static (int Index, string Tag, bool Enter) FindEarliestMarker(string s)
    {
        var best = -1; string bestTag = ""; var bestEnter = false;
        foreach (var (tag, enter) in Markers)
        {
            var i = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) { best = i; bestTag = tag; bestEnter = enter; }
        }
        return (best, bestTag, bestEnter);
    }

    /// <summary>Długość najdłuższego SUFIKSU <paramref name="s"/>, który jest PREFIKSEM któregoś markera
    /// (kandydat na taga rozciętego między deltami). 0 = nic nie trzymamy.</summary>
    private static int LongestMarkerPrefixSuffix(string s)
    {
        var max = Math.Min(MaxTagLen - 1, s.Length);
        for (var len = max; len > 0; len--)
        {
            var suffix = s[^len..];
            foreach (var (tag, _) in Markers)
                if (tag.Length > len && tag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return len;
        }
        return 0;
    }
}
