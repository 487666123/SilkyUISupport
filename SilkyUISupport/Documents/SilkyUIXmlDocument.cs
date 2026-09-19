using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

/// <summary>
/// 每个不可变编辑器快照对应一个已解析文档。弱键允许旧快照被回收；
/// Lazy 防止并发编辑功能多次解析同一快照。
/// </summary>
internal sealed class SilkyUIXmlDocument
{
    private static readonly ConditionalWeakTable<ITextSnapshot, Lazy<SilkyUIXmlDocument>> Cache = new();
    private readonly SilkyUIXmlTag[] _tags;

    public string Text { get; }
    public string FilePath { get; }
    public IReadOnlyList<SilkyUIXmlTag> Tags => _tags;
    public IReadOnlyList<string> StyleNames { get; }

    internal SilkyUIXmlDocument(string text, string filePath = null)
    {
        FilePath = filePath;
        Text = text ?? string.Empty;
        _tags = SilkyUIXmlParser.Parse(Text);
        StyleNames = Array.AsReadOnly(_tags
            .Where(tag => !tag.IsClosing && tag.Kind == SilkyUIXmlTagKind.Style)
            .Select(tag => tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Name, out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray());
    }

    public static SilkyUIXmlDocument Get(ITextSnapshot snapshot)
    {
        var path = snapshot.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
            ? document.FilePath : null;
        var parsed = Cache.GetValue(snapshot, key => new Lazy<SilkyUIXmlDocument>(
            () => new SilkyUIXmlDocument(key.GetText(), path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (string.Equals(parsed.FilePath, path, StringComparison.OrdinalIgnoreCase)) return parsed;

        // Save As 可以改变所属项目而不改变文本快照。
        Cache.Remove(snapshot);
        return Cache.GetValue(snapshot, key => new Lazy<SilkyUIXmlDocument>(
            () => new SilkyUIXmlDocument(key.GetText(), path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public SilkyUIXmlTag GetTagAtPosition(int position)
    {
        var index = FindLastStartAtOrBefore(position);
        return index >= 0 && position < _tags[index].End ? _tags[index] : null;
    }

    public SilkyUIXmlTag GetTagAtCaret(int position)
    {
        if (position <= 0 || position > Text.Length) return null;
        // 光标位于字符之间：在下一个 '<' 处，前一个未完成的标签仍拥有光标。
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

    /// <summary>获取指定标签所属元素的父标签；tagStart 必须是标签的起始位置。</summary>
    public SilkyUIXmlTag GetParentTag(int tagStart)
    {
        var index = FindLastStartAtOrBefore(tagStart);
        return index >= 0 && _tags[index].Start == tagStart ? _tags[index].Parent : null;
    }

    /// <summary>
    /// 找到文档的根 Body 标签。
    /// </summary>
    public SilkyUIXmlTag GetBodyTag()
    {
        foreach (var tag in _tags)
        {
            if (!tag.IsClosing && tag.Kind == SilkyUIXmlTagKind.Body)
                return tag;
        }
        return null;
    }
}
