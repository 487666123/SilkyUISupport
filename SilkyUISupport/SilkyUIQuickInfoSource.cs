using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("SilkyUI XML quick info source")]
[ContentType("SilkyUI XML")]
internal sealed class SilkyUIQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    [Import]
    internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        => new SilkyUIQuickInfoSource(textBuffer, MetadataService);
}

internal sealed class SilkyUIQuickInfoSource(ITextBuffer textBuffer, SilkyUIMetadataService metadataService) : IAsyncQuickInfoSource
{
    private readonly ITextBuffer _textBuffer = textBuffer;
    private readonly SilkyUIMetadataService _metadataService = metadataService;

    public Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
    {
        var triggerPoint = session.GetTriggerPoint(_textBuffer.CurrentSnapshot);
        if (!triggerPoint.HasValue)
            return Task.FromResult<QuickInfoItem>(null);

        var snapshot = triggerPoint.Value.Snapshot;
        var position = triggerPoint.Value.Position;

        if (!SilkyUIXmlSymbolResolver.TryResolve(snapshot, position, _metadataService, out var resolution))
            return Task.FromResult<QuickInfoItem>(null);

        var applicableTo = snapshot.CreateTrackingSpan(resolution.SymbolSpan, SpanTrackingMode.EdgeInclusive);
        return Task.FromResult(new QuickInfoItem(applicableTo, BuildContent(resolution)));
    }

    public void Dispose()
    {
    }

    private static object BuildContent(SilkyUISymbolResolution resolution)
    {
        return resolution.Kind switch
        {
            SilkyUISymbolKind.Element => BuildElementContent(resolution),
            SilkyUISymbolKind.Attribute => BuildAttributeContent(resolution),
            _ => ClassifiedTextElement.CreatePlainText(resolution.SymbolName)
        };
    }

    private static ContainerElement BuildElementContent(SilkyUISymbolResolution resolution)
    {
        var silkyUiClass = resolution.SilkyUiClass;
        var lines = new List<object>
        {
            ClassifiedTextElement.CreatePlainText($"元素: <{silkyUiClass.Name}>"),
            ClassifiedTextElement.CreatePlainText($"类型: {silkyUiClass.FullName}"),
            ClassifiedTextElement.CreatePlainText($"属性数: {silkyUiClass.Properties.Length}")
        };

        if (silkyUiClass.Properties.Length > 0)
        {
            var propertyPreview = string.Join(", ", silkyUiClass.Properties.Take(8).Select(property => property.Name));
            if (silkyUiClass.Properties.Length > 8)
                propertyPreview += ", ...";

            lines.Add(ClassifiedTextElement.CreatePlainText($"可用属性: {propertyPreview}"));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, lines);
    }

    private static ContainerElement BuildAttributeContent(SilkyUISymbolResolution resolution)
    {
        var property = resolution.SilkyUiProperty;
        var lines = new List<object>
        {
            ClassifiedTextElement.CreatePlainText($"属性: {resolution.CurrentTag}.{property.Name}"),
            ClassifiedTextElement.CreatePlainText($"声明类型: {property.DeclaringTypeName}"),
            ClassifiedTextElement.CreatePlainText($"属性类型: {property.TypeName}")
        };

        if (property.Enums.Length > 0)
        {
            var enumPreview = string.Join(", ", property.Enums.Take(10));
            if (property.Enums.Length > 10)
                enumPreview += ", ...";

            lines.Add(ClassifiedTextElement.CreatePlainText($"枚举值: {enumPreview}"));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, lines);
    }
}
