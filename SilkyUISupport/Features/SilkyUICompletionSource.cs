using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Name("SilkyUI XML completion source")]
[Export(typeof(ICompletionSourceProvider))]
[ContentType("SilkyUI XML")]
internal sealed class SilkyUICompletionSourceProvider : ICompletionSourceProvider
{
    [Import] public IGlyphService GlyphService { get; set; } = null!;
    [Import] public SilkyUIMetadataService MetadataService { get; set; } = null!;
    ICompletionSource ICompletionSourceProvider.TryCreateCompletionSource(ITextBuffer textBuffer)
        => new SilkyUICompletionSource(textBuffer, MetadataService);
}

/// <summary>Visual Studio 适配器：仅负责会话、跟踪和图标映射。</summary>
internal sealed class SilkyUICompletionSource(ITextBuffer textBuffer, SilkyUIMetadataService metadataService) : ICompletionSource
{
    private readonly ITextBuffer _textBuffer = textBuffer;
    private readonly SilkyUIMetadataService _metadataService = metadataService;
    private bool _disposed;

    void ICompletionSource.AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        var point = session.TextView.Caret.Position.BufferPosition;
        var context = XmlContextAnalyzer.Analyze(point.Snapshot, point.Position);
        var model = new SilkyUISemanticModel(SilkyUIXmlDocument.Get(point.Snapshot), _metadataService.GetSnapshot());
        var items = SilkyUICompletionEngine.GetItems(model, context, model.Document.StyleNames);
        if (items.Length == 0) return;

        var completions = items.Select(ToVsCompletion).Cast<Completion>().ToList();
        if (completionSets.Count > 0)
        {
            completions.AddRange(completionSets[0].Completions);
            completionSets.RemoveAt(0);
        }
        completionSets.Insert(0, new SilkyUICompletionSet("SilkyUI", "SilkyUI", FindTokenSpanAtPosition(session, context), completions));
    }

    private static Completion ToVsCompletion(SilkyUICompletionItem item)
    {
        var moniker = item.Kind switch
        {
            SilkyUICompletionItemKind.Class => KnownMonikers.Class,
            SilkyUICompletionItemKind.Enumeration => KnownMonikers.Enumeration,
            _ => KnownMonikers.Property
        };
        return new Completion4(item.DisplayText, item.InsertionText, item.Description, moniker, suffix: item.Suffix);
    }

    private static ITrackingSpan FindTokenSpanAtPosition(ICompletionSession session, XmlContext context)
    {
        var point = session.TextView.Caret.Position.BufferPosition;
        var snapshot = point.Snapshot;
        var isNamespaceUri = context.ContextType == XmlContextType.AttributeValue &&
                             SilkyUIXmlSyntax.IsNamespaceDeclaration(context.CurrentAttribute);
        var start = point.Position;
        var end = point.Position;
        while (start > 0 && IsCompletionCharacter(snapshot[start - 1], isNamespaceUri)) start--;
        while (end < snapshot.Length && IsCompletionCharacter(snapshot[end], isNamespaceUri)) end++;
        return snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeInclusive);
    }

    private static bool IsCompletionCharacter(char value, bool isNamespaceUri)
    {
        if (char.IsLetterOrDigit(value)) return true;
        return isNamespaceUri
            ? value is ':' or '/' or '.' or '-' or '_' or '~' or '?' or '&' or '=' or '%' or '#'
            : value is ':' or '.' or '_' or '-';
    }

    public void Dispose()
    {
        if (_disposed) return;
        GC.SuppressFinalize(this);
        _disposed = true;
    }
}
