using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(IClassifierProvider))]
[ContentType("SilkyUI XML")]
internal sealed class SilkyUIAttributeClassifierProvider : IClassifierProvider
{
    [Import]
    internal IClassificationTypeRegistryService ClassificationRegistry { get; set; } = null!;

    [Import]
    internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    public IClassifier GetClassifier(ITextBuffer textBuffer)
        => textBuffer.Properties.GetOrCreateSingletonProperty(
            () => new SilkyUIAttributeClassifier(
                textBuffer,
                MetadataService,
                ClassificationRegistry));
}

internal sealed class SilkyUIAttributeClassifier : IClassifier
{
    private readonly ITextBuffer _buffer;
    private readonly SilkyUIMetadataService _metadataService;
    private readonly IClassificationType _elementType;
    private readonly IClassificationType _unknownElementType;
    private readonly IClassificationType _attributeType;
    private readonly IClassificationType _specialAttributeType;
    private readonly IClassificationType _unknownAttributeType;

    public SilkyUIAttributeClassifier(
        ITextBuffer buffer,
        SilkyUIMetadataService metadataService,
        IClassificationTypeRegistryService classificationRegistry)
    {
        _buffer = buffer;
        _metadataService = metadataService;

        _elementType = classificationRegistry.GetClassificationType(
            PredefinedClassificationTypeNames.Type);

        _unknownElementType = classificationRegistry.GetClassificationType(
            PredefinedClassificationTypeNames.MismatchedBrace);

        _attributeType = classificationRegistry.GetClassificationType(
            PredefinedClassificationTypeNames.MarkupAttribute);
        _specialAttributeType = classificationRegistry.GetClassificationType(
            PredefinedClassificationTypeNames.Keyword);

        _unknownAttributeType = classificationRegistry.GetClassificationType(
            PredefinedClassificationTypeNames.String);

        _buffer.Changed += OnBufferChanged;
        SilkyUIMetadataSubscription.Subscribe(_metadataService, this,
            static subscriber => subscriber.OnMetadataRefreshed());
    }

    public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan requestedSpan)
    {
        var result = new List<ClassificationSpan>();
        var snapshot = requestedSpan.Snapshot;
        var document = SilkyUIXmlDocument.Get(snapshot);
        foreach (var tag in document.GetTags(requestedSpan.Span))
        {
            if (tag.Name.Length == 0) continue;
            var mappedClass = tag.Kind == SilkyUIXmlTagKind.Ordinary
                ? _metadataService.GetClassByName(tag.Name) : null;
            var isKnown = tag.Kind is SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Member or SilkyUIXmlTagKind.Style ||
                          mappedClass != null;
            AddSpan(tag.NameStart, tag.Name.Length, isKnown ? _elementType : _unknownElementType);
            if (tag.IsClosing) continue;

            var bodyClass = tag.Kind == SilkyUIXmlTagKind.Body ? ResolveBodyClass(tag) : null;
            var memberProperties = tag.Kind == SilkyUIXmlTagKind.Member
                ? ResolveMemberProperties(document, tag)
                : null;
            foreach (var attribute in tag.Attributes)
            {
                var type = _unknownAttributeType;
                if (SilkyUIXmlSyntax.IsNamespaceDeclaration(attribute.Name))
                    type = _attributeType;
                else if (isKnown && (SilkyUIXmlSyntax.GetSuiAttributeKind(tag.Scope, attribute.Name) != SilkyUIAttributeKind.None ||
                         SilkyUIXmlSyntax.TryGetBindingPropertyName(tag.Scope, attribute.Name, out _)))
                    type = _specialAttributeType;
                else if (bodyClass?.Properties.Any(property => property.Property.Name == attribute.Name) == true ||
                         mappedClass?.Properties.Any(property => property.Property.Name == attribute.Name) == true ||
                         memberProperties?.Any(property => property.Name == attribute.Name) == true)
                    type = _attributeType;
                AddSpan(attribute.NameStart, attribute.Name.Length, type);
            }
        }
        return result;

        void AddSpan(int start, int length, IClassificationType type)
        {
            var span = new SnapshotSpan(snapshot, start, length);
            if (requestedSpan.IntersectsWith(span)) result.Add(new ClassificationSpan(span, type));
        }
    }

    private IEnumerable<IPropertySymbol> ResolveMemberProperties(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag)
    {
        if (memberTag.Name.Length <= 2) yield break;

        var parentTag = document.GetParentTag(memberTag.Start);
        IEnumerable<SilkyUIProperty> parentProperties = null;
        if (parentTag?.Kind == SilkyUIXmlTagKind.Body)
            parentProperties = ResolveBodyClass(document.GetBodyTag())?.Properties;
        else if (parentTag?.Kind == SilkyUIXmlTagKind.Ordinary)
            parentProperties = _metadataService.GetClassByName(parentTag.Name)?.Properties;

        var memberProperty = parentProperties?.FirstOrDefault(property =>
            property.Property.Name == memberTag.Name.Substring(2));
        if (memberProperty?.Property.Type is not INamedTypeSymbol memberType)
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = memberType; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!seen.Add(property.Name) || property.IsStatic ||
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                    continue;

                yield return property;
            }
        }
    }

    private SilkyUIElementGroupClass ResolveBodyClass(SilkyUIXmlTag tag)
    {
        if (!tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className)) return null;
        return _metadataService.GetAllGroupClasses().FirstOrDefault(groupClass =>
            groupClass.FullName == className || groupClass.Name == className);
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        => ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));

    private void OnMetadataRefreshed()
    {
        var snapshot = _buffer.CurrentSnapshot;
        ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

}
