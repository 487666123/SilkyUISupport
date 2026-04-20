using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

internal enum SilkyUISymbolKind
{
    Element,
    Attribute
}

internal readonly struct SilkyUISymbolResolution(
    SilkyUISymbolKind kind, Span symbolSpan,
    string symbolName, string currentTag,
    SilkyUIClass silkyUiClass, SilkyUIProperty silkyUiProperty)
{
    public SilkyUISymbolKind Kind { get; } = kind;

    public Span SymbolSpan { get; } = symbolSpan;

    public string SymbolName { get; } = symbolName;

    public string CurrentTag { get; } = currentTag;

    public SilkyUIClass SilkyUiClass { get; } = silkyUiClass;

    public SilkyUIProperty SilkyUiProperty { get; } = silkyUiProperty;
}

internal static class SilkyUIXmlSymbolResolver
{
    public static bool TryResolve(
        ITextSnapshot snapshot,
        int position,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolResolution resolution)
    {
        resolution = default;

        if (snapshot == null || metadataService == null || snapshot.Length == 0 || position < 0 || position >= snapshot.Length)
            return false;

        if (!IsXmlNameChar(snapshot[position]))
            return false;

        var start = position;
        while (start > 0 && IsXmlNameChar(snapshot[start - 1]))
        {
            start--;
        }

        var end = position + 1;
        while (end < snapshot.Length && IsXmlNameChar(snapshot[end]))
        {
            end++;
        }

        var symbolSpan = Span.FromBounds(start, end);
        var symbolName = snapshot.GetText(symbolSpan);
        if (string.IsNullOrWhiteSpace(symbolName))
            return false;

        if (TryResolveElement(snapshot, start, symbolSpan, symbolName, metadataService, out resolution))
            return true;

        if (TryResolveAttribute(snapshot, start, end, symbolSpan, symbolName, metadataService, out resolution))
            return true;

        return false;
    }

    private static bool TryResolveElement(
        ITextSnapshot snapshot,
        int start,
        Span symbolSpan,
        string elementName,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolResolution resolution)
    {
        resolution = default;

        var isStartTagName = start > 0 && snapshot[start - 1] == '<';
        var isEndTagName = start > 1 && snapshot[start - 2] == '<' && snapshot[start - 1] == '/';
        if (!isStartTagName && !isEndTagName)
            return false;

        var silkyUiClass = metadataService.GetClassByName(elementName);
        if (silkyUiClass == null)
            return false;

        resolution = new SilkyUISymbolResolution(
            SilkyUISymbolKind.Element,
            symbolSpan,
            elementName,
            elementName,
            silkyUiClass,
            null);

        return true;
    }

    private static bool TryResolveAttribute(
        ITextSnapshot snapshot,
        int start,
        int end,
        Span symbolSpan,
        string attributeName,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolResolution resolution)
    {
        resolution = default;

        var tagStart = FindTagStart(snapshot, start);
        if (tagStart < 0)
            return false;

        if (tagStart + 1 < snapshot.Length && snapshot[tagStart + 1] == '/')
            return false;

        if (IsTagNameToken(snapshot, tagStart, start))
            return false;

        var nextNonWhitespace = SkipWhitespaceForward(snapshot, end);
        if (nextNonWhitespace >= snapshot.Length || snapshot[nextNonWhitespace] != '=')
            return false;

        var currentTag = GetTagName(snapshot, tagStart);
        if (string.IsNullOrWhiteSpace(currentTag))
            return false;

        var silkyUiClass = metadataService.GetClassByName(currentTag);
        if (silkyUiClass == null)
            return false;

        var silkyUiProperty = metadataService.GetPropertyByName(currentTag, attributeName);
        if (silkyUiProperty == null)
            return false;

        resolution = new SilkyUISymbolResolution(
            SilkyUISymbolKind.Attribute,
            symbolSpan,
            attributeName,
            currentTag,
            silkyUiClass,
            silkyUiProperty);

        return true;
    }

    private static bool IsXmlNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ':';

    private static int FindTagStart(ITextSnapshot snapshot, int position)
    {
        var quoteChar = '\0';

        for (var i = position - 1; i >= 0; i--)
        {
            var current = snapshot[i];
            if (quoteChar != '\0')
            {
                if (current == quoteChar)
                    quoteChar = '\0';
                continue;
            }

            if (current is '"' or '\'')
            {
                quoteChar = current;
                continue;
            }

            if (current == '>')
                return -1;

            if (current == '<')
                return i;
        }

        return -1;
    }

    private static bool IsTagNameToken(ITextSnapshot snapshot, int tagStart, int tokenStart)
    {
        var nameStart = tagStart + 1;
        if (nameStart < snapshot.Length && snapshot[nameStart] == '/')
            nameStart++;

        return nameStart == tokenStart;
    }

    private static int SkipWhitespaceForward(ITextSnapshot snapshot, int position)
    {
        var current = position;
        while (current < snapshot.Length && char.IsWhiteSpace(snapshot[current]))
        {
            current++;
        }

        return current;
    }

    private static string GetTagName(ITextSnapshot snapshot, int tagStart)
    {
        var current = tagStart + 1;
        if (current < snapshot.Length && snapshot[current] == '/')
            return string.Empty;

        while (current < snapshot.Length && char.IsWhiteSpace(snapshot[current]))
        {
            current++;
        }

        var start = current;
        while (current < snapshot.Length && IsXmlNameChar(snapshot[current]))
        {
            current++;
        }

        return current > start ? snapshot.GetText(Span.FromBounds(start, current)) : string.Empty;
    }
}
