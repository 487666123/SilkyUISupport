using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

internal enum SilkyUISymbolKind
{
    Element,
    Member,
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
    private static IEnumerable<SilkyUIProperty> ResolveParentProperties(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag, SilkyUIMetadataService metadataService)
    {
        var parentTag = document.GetParentTag(memberTag.Start);
        if (parentTag?.Kind == SilkyUIXmlTagKind.Body)
        {
            if (!parentTag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className))
                return null;

            var group = metadataService.GetAllGroupClasses().FirstOrDefault(item =>
                item.FullName == className || item.Name == className);
            return group?.Properties;
        }

        if (parentTag?.Kind == SilkyUIXmlTagKind.Ordinary)
            return metadataService.GetClassByName(parentTag.Name)?.Properties;

        return null;
    }

    private static bool IsExpandableMemberProperty(SilkyUIProperty property)
    {
        if (property?.Property == null || property.Property.IsStatic ||
            property.Property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
            property.Property.Type is not INamedTypeSymbol type ||
            type.SpecialType != SpecialType.None ||
            type.TypeKind is not (TypeKind.Class or TypeKind.Interface))
            return false;

        return true;
    }

    private static SilkyUIProperty ResolveMemberProperty(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag, SilkyUIMetadataService metadataService)
    {
        if (memberTag?.Kind != SilkyUIXmlTagKind.Member || memberTag.Name.Length <= 2)
            return null;

        var memberName = memberTag.Name.Substring(2);
        return ResolveParentProperties(document, memberTag, metadataService)?.FirstOrDefault(property =>
            property.Property.Name == memberName && IsExpandableMemberProperty(property));
    }

    private static IEnumerable<SilkyUIProperty> ResolveMemberProperties(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag, SilkyUIMetadataService metadataService)
    {
        var memberProperty = ResolveMemberProperty(document, memberTag, metadataService);
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

                var sourceLocation = property.Locations.FirstOrDefault(location => location.IsInSource);
                var lineSpan = sourceLocation?.GetLineSpan();
                yield return new SilkyUIProperty(
                    property,
                    [],
                    lineSpan?.Path ?? string.Empty,
                    lineSpan?.StartLinePosition.Line ?? 0,
                    lineSpan?.StartLinePosition.Character ?? 0);
            }
        }
    }

    private static bool IsPublicWritableProperty(IPropertySymbol property)
        => property?.SetMethod?.DeclaredAccessibility == Accessibility.Public;

    public static bool TryResolve(
        ITextSnapshot snapshot,
        int position,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolResolution resolution)
    {
        resolution = default;
        if (snapshot == null || metadataService == null || position < 0 || position >= snapshot.Length)
            return false;

        var document = SilkyUIXmlDocument.Get(snapshot);
        var tag = document.GetTagAtPosition(position);
        if (tag == null) return false;

        if (position >= tag.NameStart && position < tag.NameStart + tag.Name.Length)
        {
            if (tag.Kind == SilkyUIXmlTagKind.Ordinary)
            {
                var mappedClass = metadataService.GetClassByName(tag.Name);
                if (mappedClass == null) return false;
                resolution = new SilkyUISymbolResolution(
                    SilkyUISymbolKind.Element, new Span(tag.NameStart, tag.Name.Length),
                    tag.Name, tag.Name, mappedClass, null);
                return true;
            }

            if (tag.Kind == SilkyUIXmlTagKind.Member &&
                ResolveMemberProperty(document, tag, metadataService) is { } memberProperty)
            {
                resolution = new SilkyUISymbolResolution(
                    SilkyUISymbolKind.Member, new Span(tag.NameStart, tag.Name.Length),
                    tag.Name, tag.Name, null, memberProperty);
                return true;
            }

            return false;
        }

        if (tag.IsClosing || tag.Kind is not (SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary or SilkyUIXmlTagKind.Member))
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
                property = bodyClass?.Properties.FirstOrDefault(item =>
                    item.Property.Name == attribute.Name && IsPublicWritableProperty(item.Property));
            }
            else if (tag.Kind == SilkyUIXmlTagKind.Ordinary)
            {
                mappedClass = metadataService.GetClassByName(tag.Name);
                property = mappedClass?.Properties.FirstOrDefault(item =>
                    item.Property.Name == attribute.Name && IsPublicWritableProperty(item.Property));
            }
            else
            {
                property = ResolveMemberProperties(document, tag, metadataService)?.FirstOrDefault(item =>
                    item.Property.Name == attribute.Name);
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
