using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
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
        var xmlMappingClass = resolution.SilkyUiClass;
        var lines = new List<object>
        {
            BuildSignatureLine(
                KnownMonikers.Class,
                "class",
                xmlMappingClass.Alias,
                xmlMappingClass.Class.ToDisplayString()),
            BuildMetadataLine("属性", xmlMappingClass.Properties.Length.ToString())
        };

        if (xmlMappingClass.Properties.Length > 0)
        {
            var propertyPreview = string.Join(", ", xmlMappingClass.Properties.Take(8).Select(property => property.Property.Name));
            if (xmlMappingClass.Properties.Length > 8)
                propertyPreview += ", ...";

            lines.Add(BuildMetadataLine("可用属性", propertyPreview));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, lines);
    }

    private static ContainerElement BuildAttributeContent(SilkyUISymbolResolution resolution)
    {
        var property = resolution.SilkyUiProperty;
        var lines = new List<object>
        {
            BuildSignatureLine(
                KnownMonikers.Property,
                property.Property.Type.ToDisplayString(),
                $"{resolution.CurrentTag}.{property.Property.Name}",
                string.Empty),
            BuildMetadataLine("声明类型", property.Property.ContainingType.ToDisplayString()),
            BuildMetadataLine("属性类型", property.Property.Type.ToDisplayString())
        };

        if (property.Enums.Length > 0)
        {
            var enumPreview = string.Join(", ", property.Enums.Take(10));
            if (property.Enums.Length > 10)
                enumPreview += ", ...";

            lines.Add(BuildMetadataLine("枚举值", enumPreview));
        }

        return new ContainerElement(ContainerElementStyle.Stacked, lines);
    }

    private static ContainerElement BuildSignatureLine(
        Microsoft.VisualStudio.Imaging.Interop.ImageMoniker moniker,
        string prefix,
        string name,
        string detail)
    {
        var runs = new List<ClassifiedTextRun>();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, prefix));
            runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.WhiteSpace, " "));
        }

        runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, name));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.WhiteSpace, "  "));
            runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, detail));
        }

        return new ContainerElement(
            ContainerElementStyle.Wrapped,
            new object[]
            {
                new ImageElement(new ImageId(moniker.Guid, moniker.Id)),
                new ClassifiedTextElement(runs)
            });
    }

    private static ClassifiedTextElement BuildMetadataLine(string label, string value)
    {
        return new ClassifiedTextElement(
        [
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, $"{label}: "),
            new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, value)
        ]);
    }
}
