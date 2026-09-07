using System;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

internal enum XmlContextType { Unknown, TagName, AttributeName, AttributeValue }

internal class XmlContext
{
    public XmlContextType ContextType { get; set; }
    public string CurrentTag { get; set; } = string.Empty;
    public string CurrentAttribute { get; set; } = string.Empty;
    public int TagStart { get; set; } = -1;
    public int TagEnd { get; set; } = -1;
    public SilkyUIXmlTag Tag { get; set; }
}

internal static class XmlContextAnalyzer
{
    public static XmlContext Analyze(ICompletionSession session)
    {
        var point = session.TextView.Caret.Position.BufferPosition;
        return Analyze(point.Snapshot, point.Position);
    }

    public static XmlContext Analyze(ITextSnapshot snapshot, int position)
        => Analyze(SilkyUIXmlDocument.Get(snapshot), position);

    internal static XmlContext Analyze(string text, int position)
        => Analyze(new SilkyUIXmlDocument(text), position);

    private static XmlContext Analyze(SilkyUIXmlDocument document, int position)
    {
        var context = new XmlContext();
        position = Math.Min(position, document.Text.Length);
        var current = document.GetTagAtCaret(position);
        if (current == null) return context;

        context.TagStart = current.Start;
        context.TagEnd = current.IsComplete ? current.ContentEnd : -1;
        // Complete start tags use all their declarations. An unfinished tag only exposes tokens before the caret.
        context.Tag = current.IsComplete ? current : SilkyUIXmlSyntax.GetIncompletePrefix(current, position);
        context.CurrentTag = current.Name;
        if (position <= current.NameStart + current.Name.Length)
        {
            context.CurrentTag = current.Name.Substring(0, Math.Max(0, position - current.NameStart));
            context.ContextType = XmlContextType.TagName;
            return context;
        }
        if (current.IsClosing) return context;

        context.ContextType = XmlContextType.AttributeName;
        foreach (var attribute in current.Attributes)
        {
            if (attribute.NameStart > position) break;
            if (attribute.ValueStart >= 0 && position >= attribute.ValueStart &&
                position <= attribute.ValueStart + attribute.Value.Length)
            {
                context.ContextType = XmlContextType.AttributeValue;
                context.CurrentAttribute = attribute.Name;
                return context;
            }
            if (position >= attribute.NameStart && position <= attribute.NameEnd)
            {
                context.CurrentAttribute = attribute.Name.Substring(0, position - attribute.NameStart);
                return context;
            }
        }
        return context;
    }
}
