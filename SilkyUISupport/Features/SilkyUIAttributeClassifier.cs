using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(IClassifierProvider))]
[ContentType("SilkyUI XML")]
internal sealed class SilkyUIAttributeClassifierProvider : IClassifierProvider
{
    [Import] internal IClassificationTypeRegistryService ClassificationRegistry { get; set; } = null!;
    [Import] internal SilkyUIMetadataService MetadataService { get; set; } = null!;
    public IClassifier GetClassifier(ITextBuffer textBuffer) => textBuffer.Properties.GetOrCreateSingletonProperty(
        () => new SilkyUIAttributeClassifier(textBuffer, MetadataService, ClassificationRegistry));
}

internal sealed class SilkyUIAttributeClassifier : IClassifier
{
    private readonly ITextBuffer _buffer;
    private readonly SilkyUIMetadataService _metadataService;
    private readonly SilkyUIAnalysisCache<SilkyUIClassification> _classifications = new(SilkyUIClassificationService.GetClassifications);
    private readonly IClassificationType _elementType;
    private readonly IClassificationType _unknownElementType;
    private readonly IClassificationType _attributeType;
    private readonly IClassificationType _specialAttributeType;
    private readonly IClassificationType _unknownAttributeType;

    public SilkyUIAttributeClassifier(ITextBuffer buffer, SilkyUIMetadataService metadataService, IClassificationTypeRegistryService registry)
    {
        _buffer = buffer;
        _metadataService = metadataService;
        _elementType = registry.GetClassificationType(PredefinedClassificationTypeNames.Type);
        _unknownElementType = registry.GetClassificationType(PredefinedClassificationTypeNames.MismatchedBrace);
        _attributeType = registry.GetClassificationType(PredefinedClassificationTypeNames.MarkupAttribute);
        _specialAttributeType = registry.GetClassificationType(PredefinedClassificationTypeNames.Keyword);
        _unknownAttributeType = registry.GetClassificationType(PredefinedClassificationTypeNames.String);
        _buffer.Changed += OnBufferChanged;
        SilkyUIMetadataSubscription.Subscribe(_metadataService, this, static classifier => classifier.OnMetadataRefreshed());
    }

    public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan requestedSpan)
    {
        var snapshot = requestedSpan.Snapshot;
        var classifications = _classifications.GetResults(SilkyUIXmlDocument.Get(snapshot), _metadataService.GetSnapshot());
        var result = new List<ClassificationSpan>();
        foreach (var classification in classifications)
        {
            var span = new SnapshotSpan(snapshot, classification.Start, classification.Length);
            if (requestedSpan.IntersectsWith(span)) result.Add(new ClassificationSpan(span, GetType(classification.Kind)));
        }
        return result;
    }

    private IClassificationType GetType(SilkyUIClassificationKind kind) => kind switch
    {
        SilkyUIClassificationKind.Element => _elementType,
        SilkyUIClassificationKind.UnknownElement => _unknownElementType,
        SilkyUIClassificationKind.Attribute => _attributeType,
        SilkyUIClassificationKind.SpecialAttribute => _specialAttributeType,
        _ => _unknownAttributeType
    };

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => RaiseChanged(e.After);
    private void OnMetadataRefreshed() => RaiseChanged(_buffer.CurrentSnapshot);
    private void RaiseChanged(ITextSnapshot snapshot) => ClassificationChanged?.Invoke(this,
        new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
}
