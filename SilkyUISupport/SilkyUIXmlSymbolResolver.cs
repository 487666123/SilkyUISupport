using System.Linq;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

internal enum SilkyUISymbolKind
{
    Element,
    Attribute
}

internal readonly struct SilkyUISymbolResolution(
    SilkyUISymbolKind kind, Span symbolSpan,
    string symbolName, string currentTag,
    XmlMappingClass silkyUiClass, SilkyUIProperty silkyUiProperty,
    SilkyUIElementGroupClass bodyClass = null)
{
    public SilkyUISymbolKind Kind { get; } = kind;
    public Span SymbolSpan { get; } = symbolSpan;
    public string SymbolName { get; } = symbolName;
    public string CurrentTag { get; } = currentTag;
    public XmlMappingClass SilkyUiClass { get; } = silkyUiClass;
    public SilkyUIProperty SilkyUiProperty { get; } = silkyUiProperty;
    public SilkyUIElementGroupClass BodyClass { get; } = bodyClass;
}

internal static class SilkyUIXmlSymbolResolver
{
    public static bool TryResolve(
        ITextSnapshot snapshot,
        int position,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolResolution resolution)
    {
        resolution = default;
        if (snapshot == null || metadataService == null || position < 0 || position >= snapshot.Length)
            return false;

        var tag = SilkyUIXmlDocument.Get(snapshot).GetTagAtPosition(position);
        if (tag == null) return false;

        if (position >= tag.NameStart && position < tag.NameStart + tag.Name.Length)
        {
            if (tag.Kind != SilkyUIXmlTagKind.Ordinary) return false;
            var mappedClass = metadataService.GetClassByName(tag.Name);
            if (mappedClass == null) return false;
            resolution = new SilkyUISymbolResolution(
                SilkyUISymbolKind.Element, new Span(tag.NameStart, tag.Name.Length),
                tag.Name, tag.Name, mappedClass, null);
            return true;
        }

        if (tag.IsClosing || tag.Kind is not (SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary))
            return false;
        foreach (var attribute in tag.Attributes)
        {
            if (position < attribute.NameStart || position >= attribute.NameEnd) continue;
            if (attribute.ValueStart < 0 || SilkyUIXmlSyntax.IsNamespaceDeclaration(attribute.Name) ||
                attribute.Name.IndexOf(':') >= 0)
                return false;

            XmlMappingClass mappedClass = null;
            SilkyUIElementGroupClass bodyClass = null;
            SilkyUIProperty property;
            if (tag.Kind == SilkyUIXmlTagKind.Body)
            {
                if (!tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className)) return false;
                var groups = metadataService.GetAllGroupClasses();
                bodyClass = groups.FirstOrDefault(group => group.FullName == className)
                    ?? groups.FirstOrDefault(group => group.Name == className);
                property = bodyClass?.Properties.FirstOrDefault(item => item.Property.Name == attribute.Name);
            }
            else
            {
                mappedClass = metadataService.GetClassByName(tag.Name);
                property = mappedClass?.Properties.FirstOrDefault(item => item.Property.Name == attribute.Name);
            }
            if (property == null) return false;

            // Navigation already consumes the property's source location, including inherited Body properties.
            resolution = new SilkyUISymbolResolution(
                SilkyUISymbolKind.Attribute, new Span(attribute.NameStart, attribute.Name.Length),
                attribute.Name, tag.Name, mappedClass, property, bodyClass);
            return true;
        }
        return false;
    }
}
