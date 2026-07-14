using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
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
        _metadataService.Refreshed += OnMetadataRefreshed;
    }

    public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan requestedSpan)
    {
        var tags = new List<ClassificationSpan>();

        var snapshot = requestedSpan.Snapshot;
        var text = snapshot.GetText();
        var position = 0;

        while (position < text.Length)
        {
            var tagStart = text.IndexOf('<', position);
            if (tagStart < 0)
                break;

            if (tagStart + 1 >= text.Length)
                break;

            var next = text[tagStart + 1];
            if (next is '?' or '!')
            {
                var terminator = next == '?' ? "?>" : "-->";
                var end = text.IndexOf(terminator, tagStart + 2, StringComparison.Ordinal);
                if (end < 0)
                    break;

                position = end + terminator.Length;
                continue;
            }

            if (next == '/')
            {
                var closingTagEnd = text.IndexOf('>', tagStart + 2);
                if (closingTagEnd < 0)
                    break;

                var closingTagNameStart = tagStart + 2;
                var closingTagNameEnd = ReadNameEnd(text, closingTagNameStart, closingTagEnd);
                AddElementClassification(
                    tags,
                    requestedSpan,
                    snapshot,
                    text,
                    closingTagNameStart,
                    closingTagNameEnd);
                position = closingTagEnd + 1;
                continue;
            }

            var tagEnd = FindTagEnd(text, tagStart + 1);
            if (tagEnd < 0)
                break;

            var tagNameStart = tagStart + 1;
            var tagNameEnd = ReadNameEnd(text, tagNameStart, tagEnd);
            var tagName = text.Substring(tagNameStart, tagNameEnd - tagNameStart);
            AddElementClassification(tags, requestedSpan, snapshot, text, tagNameStart, tagNameEnd);
            var mappedClass = tagName == "Body" ? null : _metadataService.GetClassByName(tagName);
            var bodyClass = tagName == "Body" ? ResolveBodyClass(text, tagStart, tagEnd) : null;

            var attributePosition = tagNameEnd;
            while (attributePosition < tagEnd)
            {
                while (attributePosition < tagEnd && char.IsWhiteSpace(text[attributePosition]))
                    attributePosition++;

                if (attributePosition >= tagEnd || text[attributePosition] == '/')
                    break;

                var nameStart = attributePosition;
                while (attributePosition < tagEnd && IsAttributeNameChar(text[attributePosition]))
                    attributePosition++;

                if (nameStart == attributePosition)
                {
                    attributePosition++;
                    continue;
                }

                var attributeName = text.Substring(nameStart, attributePosition - nameStart);
                var classificationType = GetClassificationType(tagName, attributeName, mappedClass, bodyClass);
                var span = new SnapshotSpan(snapshot, nameStart, attributePosition - nameStart);
                if (requestedSpan.IntersectsWith(span))
                    tags.Add(new ClassificationSpan(span, classificationType));

                attributePosition = SkipAttributeValue(text, attributePosition, tagEnd);
            }

            position = tagEnd + 1;
        }

        return tags;
    }

    private void AddElementClassification(
        ICollection<ClassificationSpan> tags,
        SnapshotSpan requestedSpan,
        ITextSnapshot snapshot,
        string text,
        int nameStart,
        int nameEnd)
    {
        if (nameEnd <= nameStart)
            return;

        var elementName = text.Substring(nameStart, nameEnd - nameStart);
        var classificationType = IsKnownElement(elementName) ? _elementType : _unknownElementType;
        var elementSpan = new SnapshotSpan(snapshot, nameStart, nameEnd - nameStart);
        if (requestedSpan.IntersectsWith(elementSpan))
            tags.Add(new ClassificationSpan(elementSpan, classificationType));
    }

    private bool IsKnownElement(string elementName)
    {
        return elementName is "Body" or "Style" ||
               elementName.StartsWith("M.", StringComparison.Ordinal) ||
               elementName.StartsWith("Style.", StringComparison.Ordinal) ||
               _metadataService.GetClassByName(elementName) != null;
    }

    private IClassificationType GetClassificationType(
        string tagName,
        string attributeName,
        XmlMappingClass mappedClass,
        SilkyUIElementGroupClass bodyClass)
    {
        if (attributeName is "Name" or "Class" or "Style" || attributeName.StartsWith("Bind.", StringComparison.Ordinal))
            return _specialAttributeType;

        if (tagName == "Body")
            return bodyClass?.Properties.Any(property => property.Property.Name == attributeName) == true
                ? _attributeType
                : _unknownAttributeType;

        return mappedClass?.Properties.Any(property => property.Property.Name == attributeName) == true
            ? _attributeType
            : _unknownAttributeType;
    }

    private SilkyUIElementGroupClass ResolveBodyClass(string text, int tagStart, int tagEnd)
    {
        var tagSection = text.Substring(tagStart, tagEnd - tagStart);
        var classIndex = tagSection.IndexOf("Class=", StringComparison.OrdinalIgnoreCase);
        if (classIndex < 0)
            return null;

        var valueStart = classIndex + "Class=".Length;
        if (valueStart >= tagSection.Length || tagSection[valueStart] is not ('"' or '\''))
            return null;

        var quote = tagSection[valueStart++];
        var valueEnd = tagSection.IndexOf(quote, valueStart);
        if (valueEnd <= valueStart)
            return null;

        var className = tagSection.Substring(valueStart, valueEnd - valueStart);
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

    private static int FindTagEnd(string text, int start)
    {
        var quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            if (quote != '\0')
            {
                if (text[i] == quote)
                    quote = '\0';
                continue;
            }

            if (text[i] is '"' or '\'')
                quote = text[i];
            else if (text[i] == '>')
                return i;
        }

        return -1;
    }

    private static int ReadNameEnd(string text, int start, int end)
    {
        var current = start;
        while (current < end && !char.IsWhiteSpace(text[current]) && text[current] != '/')
            current++;
        return current;
    }

    private static int SkipAttributeValue(string text, int start, int end)
    {
        var current = start;
        while (current < end && char.IsWhiteSpace(text[current]))
            current++;

        if (current >= end || text[current] != '=')
            return current;

        current++;
        while (current < end && char.IsWhiteSpace(text[current]))
            current++;

        if (current < end && text[current] is '"' or '\'')
        {
            var quote = text[current++];
            while (current < end && text[current] != quote)
                current++;
            if (current < end)
                current++;
        }

        return current;
    }

    private static bool IsAttributeNameChar(char value)
        => char.IsLetterOrDigit(value) || value is '.' or '_' or '-';
}
