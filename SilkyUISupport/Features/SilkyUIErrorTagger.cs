using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(ITaggerProvider))]
[Name("SilkyUI XML error tagger")]
[ContentType("SilkyUI XML")]
[TagType(typeof(IErrorTag))]
internal class SilkyUIErrorTaggerProvider : ITaggerProvider
{
    [Import] internal SilkyUIMetadataService MetadataService { get; set; } = null!;
    public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        => typeof(T) == typeof(IErrorTag)
            ? (ITagger<T>)(object)buffer.Properties.GetOrCreateSingletonProperty(() => new SilkyUIErrorTagger(buffer, MetadataService))
            : null;
}

internal sealed class SilkyUIErrorTagger : ITagger<IErrorTag>
{
    private readonly ITextBuffer _buffer;
    private readonly SilkyUIMetadataService _metadataService;

    public SilkyUIErrorTagger(ITextBuffer buffer, SilkyUIMetadataService metadataService)
    {
        _buffer = buffer;
        _metadataService = metadataService;
        _buffer.Changed += OnBufferChanged;
        SilkyUIMetadataSubscription.Subscribe(_metadataService, this, static tagger => tagger.OnMetadataRefreshed());
    }

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;
    private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => RaiseChanged(e.After);
    private void OnMetadataRefreshed() => RaiseChanged(_buffer.CurrentSnapshot);
    private void RaiseChanged(ITextSnapshot snapshot) => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));

    public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;
        var snapshot = spans[0].Snapshot;
        var model = new SilkyUISemanticModel(SilkyUIXmlDocument.Get(snapshot), _metadataService.GetSnapshot());
        foreach (var diagnostic in SilkyUIDiagnosticAnalyzer.Analyze(model))
        {
            var span = new SnapshotSpan(snapshot, diagnostic.Start, diagnostic.Length);
            if (spans.IntersectsWith(span))
                yield return new TagSpan<IErrorTag>(span, new ErrorTag(PredefinedErrorTypeNames.SyntaxError, diagnostic.Message));
        }
    }
}
