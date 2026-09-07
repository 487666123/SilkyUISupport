using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Shell;
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
    [Import]
    internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) == typeof(IErrorTag))
            return (ITagger<T>)(object)buffer.Properties.GetOrCreateSingletonProperty(
                () => new SilkyUIErrorTagger(buffer, MetadataService));
        return null;
    }
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
        _metadataService.Refreshed += OnMetadataRefreshed;
    }

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
            new SnapshotSpan(e.After, 0, e.After.Length)));
    }

    private void OnMetadataRefreshed()
    {
        var snapshot = _buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
            new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var document = SilkyUIXmlDocument.Get(snapshot);
        foreach (var tag in document.GetTags(spans))
        {
            if (tag.IsClosing || tag.Name.Length == 0) continue;
            var suiClass = tag.Kind == SilkyUIXmlTagKind.Ordinary ? _metadataService.GetClassByName(tag.Name) : null;
            var isKnown = tag.Kind is SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Member or SilkyUIXmlTagKind.Style ||
                          suiClass != null;
            if (!isKnown)
            {
                var error = MakeError(snapshot, new Span(tag.NameStart, tag.Name.Length),
                    PredefinedErrorTypeNames.SyntaxError, $"未知元素 '{tag.Name}'");
                if (spans.IntersectsWith(error.Span)) yield return error;
                continue;
            }

            foreach (var error in ParseAndValidateAttributes(tag, snapshot, suiClass))
                if (spans.IntersectsWith(error.Span)) yield return error;
        }
    }

    private static TagSpan<IErrorTag> MakeError(ITextSnapshot snapshot, Span span, string type, string message)
        => new(new SnapshotSpan(snapshot, span), new ErrorTag(type, message));

    private IEnumerable<TagSpan<IErrorTag>> ParseAndValidateAttributes(
        SilkyUIXmlTag tag, ITextSnapshot snapshot, XmlMappingClass suiClass)
    {
        var tagName = tag.Name;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bodyClass = tag.Kind == SilkyUIXmlTagKind.Body
            ? ResolveBodyClass(tag)
            : null;
        var hasKnownProperties = tagName == "Body" ? bodyClass != null : suiClass != null;
        IEnumerable<SilkyUIProperty> properties = tagName == "Body"
            ? bodyClass?.Properties ?? []
            : suiClass?.Properties ?? [];

        foreach (var attribute in tag.Attributes)
        {
            var attrName = attribute.Name;
            var attrSpan = new Span(attribute.NameStart, attrName.Length);

            if (SilkyUIXmlSyntax.IsNamespaceDeclaration(attrName))
                continue;

            if (!seen.Add(attrName))
            {
                yield return MakeError(snapshot, attrSpan,
                    PredefinedErrorTypeNames.SyntaxError, $"重复属性 '{attrName}'");
                continue;
            }

            var kind = SilkyUIXmlSyntax.GetSuiAttributeKind(tag.Scope, attrName);
            if (kind != SilkyUIAttributeKind.None)
            {
                if (tagName == "Body" && kind == SilkyUIAttributeKind.Class &&
                    attribute.ValueComplete && !string.IsNullOrWhiteSpace(attribute.Value) &&
                    !_metadataService.GetAllGroupClasses().Any(group =>
                        group.FullName == attribute.Value || group.Name == attribute.Value))
                {
                    var valueSpan = attribute.ValueStart >= 0
                        ? new Span(attribute.ValueStart, attribute.Value.Length)
                        : attrSpan;
                    yield return MakeError(snapshot, valueSpan,
                        PredefinedErrorTypeNames.SyntaxError, $"未知类 '{attribute.Value}'");
                }

                continue;
            }

            if (SilkyUIXmlSyntax.TryGetBindingPropertyName(
                    tag.Scope, attrName, out var bindingPropertyName))
            {
                var bindingProperty = properties?.FirstOrDefault(item =>
                    item.Property.Name == bindingPropertyName);
                if (hasKnownProperties && bindingProperty == null)
                {
                    yield return MakeError(snapshot, attrSpan,
                        PredefinedErrorTypeNames.SyntaxError,
                        $"'{tagName}' 上没有可绑定的 '{bindingPropertyName}' 属性");
                }
                else if (bindingProperty != null && bindingProperty.Property.SetMethod == null)
                {
                    yield return MakeError(snapshot, attrSpan,
                        PredefinedErrorTypeNames.SyntaxError,
                        $"'{bindingPropertyName}' 不支持绑定");
                }

                continue;
            }

            if (attrName.IndexOf(':') >= 0)
            {
                yield return MakeError(snapshot, attrSpan,
                    PredefinedErrorTypeNames.SyntaxError, $"不支持命名空间属性 '{attrName}'");
                continue;
            }

            var property = properties?.FirstOrDefault(item => item.Property.Name == attrName);
            if (hasKnownProperties && property == null)
            {
                yield return MakeError(snapshot, attrSpan,
                    PredefinedErrorTypeNames.SyntaxError, $"'{tagName}' 上没有 '{attrName}' 属性");
                continue;
            }

            if (hasKnownProperties && property != null && attribute.ValueComplete && attribute.Value.Length > 0 && property.Enums.Length > 0 &&
                !property.Enums.Contains(attribute.Value, StringComparer.Ordinal))
            {
                var preview = string.Join(", ", property.Enums);
                var valueSpan = attribute.ValueStart >= 0
                    ? new Span(attribute.ValueStart, attribute.Value.Length)
                    : attrSpan;
                yield return MakeError(snapshot, valueSpan,
                    PredefinedErrorTypeNames.SyntaxError, $"'{attrName}' 可选值: {preview}");
            }
        }
    }

    private SilkyUIElementGroupClass ResolveBodyClass(SilkyUIXmlTag tag)
    {
        if (!tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className))
            return null;

        return _metadataService.GetAllGroupClasses().FirstOrDefault(group =>
            group.FullName == className || group.Name == className);
    }
}
