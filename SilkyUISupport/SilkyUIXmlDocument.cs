using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

/// <summary>
/// One parsed document per immutable editor snapshot. Weak keys allow old snapshots to be collected;
/// Lazy prevents concurrent editor features from parsing the same snapshot more than once.
/// </summary>
internal sealed class SilkyUIXmlDocument
{
    private static readonly ConditionalWeakTable<ITextSnapshot, Lazy<SilkyUIXmlDocument>> Cache = new();
    private readonly SilkyUIXmlTag[] _tags;

    public string Text { get; }
    public IReadOnlyList<string> StyleNames { get; }

    internal SilkyUIXmlDocument(string text)
    {
        Text = text ?? string.Empty;
        _tags = SilkyUIXmlSyntax.EnumerateTags(Text).ToArray();
        StyleNames = Array.AsReadOnly(_tags
            .Where(tag => !tag.IsClosing && tag.Kind == SilkyUIXmlTagKind.Style)
            .Select(tag => tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Name, out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray());
    }

    public static SilkyUIXmlDocument Get(ITextSnapshot snapshot)
        => Cache.GetValue(snapshot, key => new Lazy<SilkyUIXmlDocument>(
            () => new SilkyUIXmlDocument(key.GetText()), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public SilkyUIXmlTag GetTagAtPosition(int position)
    {
        var index = FindLastStartAtOrBefore(position);
        return index >= 0 && position < _tags[index].End ? _tags[index] : null;
    }

    public SilkyUIXmlTag GetTagAtCaret(int position)
    {
        if (position <= 0 || position > Text.Length) return null;
        // The caret is between characters: at the next '<', the previous incomplete tag still owns it.
        var index = FindLastStartAtOrBefore(position - 1);
        return index >= 0 && position <= _tags[index].ContentEnd ? _tags[index] : null;
    }

    public IEnumerable<SilkyUIXmlTag> GetTags(Span requestedSpan)
    {
        for (var i = FindFirstEndingAtOrAfter(requestedSpan.Start);
             i < _tags.Length && _tags[i].Start <= requestedSpan.End; i++)
            yield return _tags[i];
    }

    public IEnumerable<SilkyUIXmlTag> GetTags(NormalizedSnapshotSpanCollection requestedSpans)
    {
        var lastIndex = -1;
        foreach (var span in requestedSpans)
        {
            var index = Math.Max(lastIndex + 1, FindFirstEndingAtOrAfter(span.Start.Position));
            for (; index < _tags.Length && _tags[index].Start <= span.End.Position; index++)
            {
                lastIndex = index;
                yield return _tags[index];
            }
        }
    }

    private int FindLastStartAtOrBefore(int position)
    {
        var low = 0;
        var high = _tags.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_tags[middle].Start <= position) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    private int FindFirstEndingAtOrAfter(int position)
    {
        var low = 0;
        var high = _tags.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_tags[middle].End < position) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
