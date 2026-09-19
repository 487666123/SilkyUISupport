using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SilkyUISupport;

/// <summary>绑定一个 XML 文档快照与一个不可变 C# 元数据快照。</summary>
internal sealed class SilkyUISemanticModel
{
    // Tag identity keeps caret-specific projections separate from the parsed tag.
    private readonly Dictionary<SilkyUIXmlTag, SilkyUIElementInfo> _elements = new();

    public SilkyUISemanticModel(SilkyUIXmlDocument document, SilkyUIMetadataSnapshot metadata)
    {
        Document = document;
        Metadata = metadata;
        ClrProject = metadata.GetClrProject(document.FilePath);
        var root = document.Tags.FirstOrDefault(tag => !tag.IsClosing && tag.Parent == null);
        if (root != null && root.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className) &&
            !string.IsNullOrWhiteSpace(className))
            ClrAccessContext = ClrProject?.Compilation.Assembly.GetTypeByMetadataName(className);
        ClrAccessContext ??= ClrProject?.Compilation.Assembly;
    }

    public SilkyUIXmlDocument Document { get; }
    public SilkyUIMetadataSnapshot Metadata { get; }
    public SilkyUIClrProject ClrProject { get; }
    public ISymbol ClrAccessContext { get; }

    public SilkyUIElementInfo ResolveElement(SilkyUIXmlTag tag)
    {
        if (tag == null) return new(null, false, null, null, [], null);
        if (_elements.TryGetValue(tag, out var element)) return element;

        element = tag.Kind switch
        {
            SilkyUIXmlTagKind.Body => ResolveBody(tag),
            SilkyUIXmlTagKind.Ordinary => ResolveOrdinary(tag),
            SilkyUIXmlTagKind.Member => ResolveMember(tag),
            SilkyUIXmlTagKind.Style => new(tag, true, null, null, ResolveStyleProperties(tag), null),
            _ => new(tag, false, null, null, [], null)
        };
        _elements.Add(tag, element);
        return element;
    }

    public SilkyUIAttributeInfo ResolveAttribute(SilkyUIXmlTag tag, SilkyUIXmlAttribute attribute)
    {
        if (SilkyUIXmlSyntax.IsNamespaceDeclaration(attribute.Name))
            return new(attribute, SilkyUISemanticAttributeKind.Namespace, null, SilkyUIAttributeKind.None, false);

        var element = ResolveElement(tag);
        var hasKnownProperties = element.MappingClass != null || element.BodyClass != null || element.MemberProperty != null;
        var directive = SilkyUIXmlSyntax.GetSuiAttributeKind(tag?.Scope, attribute.Name);
        if (directive != SilkyUIAttributeKind.None)
            return new(attribute, SilkyUISemanticAttributeKind.Directive, null, directive, hasKnownProperties);

        if (SilkyUIXmlSyntax.TryGetBindingPropertyName(tag?.Scope, attribute.Name, out var bindingName))
        {
            var property = FindProperty(element.Properties, bindingName);
            return new(attribute, SilkyUISemanticAttributeKind.Binding, property, SilkyUIAttributeKind.None,
                hasKnownProperties);
        }

        if (attribute.Name.IndexOf(':') >= 0)
            return new(attribute, SilkyUISemanticAttributeKind.UnsupportedNamespace, null, SilkyUIAttributeKind.None,
                hasKnownProperties);

        var resolvedProperty = FindProperty(element.Properties, attribute.Name);
        return new(attribute, resolvedProperty == null
            ? SilkyUISemanticAttributeKind.Unknown
            : SilkyUISemanticAttributeKind.Property, resolvedProperty, SilkyUIAttributeKind.None,
            hasKnownProperties);
    }

    public ImmutableList<SilkyUIProperty> GetPropertiesForTag(SilkyUIXmlTag tag)
        => ResolveElement(tag).Properties;

    public ImmutableList<SilkyUIProperty> GetParentProperties(SilkyUIXmlTag tag)
        => tag?.Parent == null ? [] : ResolveElement(tag.Parent).Properties;

    public bool CanBind(SilkyUIXmlTag tag)
    {
        if (tag?.Kind != SilkyUIXmlTagKind.Member) return true;
        return IsUIViewType(ResolveElement(tag).MemberProperty?.Property.Type);
    }

    public IEnumerable<SilkyUIProperty> GetExpandableParentProperties(SilkyUIXmlTag tag)
        => GetParentProperties(tag).Where(item => IsExpandableMemberProperty(item.Property));

    public bool TryResolveSymbol(int position, out SilkyUISymbolInfo symbol)
    {
        symbol = null;
        var tag = Document.GetTagAtPosition(position);
        if (tag == null) return false;
        var element = ResolveElement(tag);

        if (position >= tag.NameStart && position < tag.NameStart + tag.Name.Length)
        {
            if (tag.Kind == SilkyUIXmlTagKind.Body)
            {
                var bodyClass = tag.IsClosing ? ResolveElement(tag.MatchingOpeningTag).BodyClass : element.BodyClass;
                if (bodyClass == null) return false;
                symbol = new(SilkyUISymbolKind.BodyClass, tag.NameStart, tag.Name.Length, tag.Name, tag.Name,
                    null, null, bodyClass);
                return true;
            }
            if (tag.Kind == SilkyUIXmlTagKind.Ordinary && element.MappingClass != null)
            {
                symbol = new(SilkyUISymbolKind.Element, tag.NameStart, tag.Name.Length, tag.Name, tag.Name,
                    element.MappingClass, null, null);
                return true;
            }
            if (tag.Kind == SilkyUIXmlTagKind.Member && element.MemberProperty != null)
            {
                symbol = new(SilkyUISymbolKind.Member, tag.NameStart, tag.Name.Length, tag.Name, tag.Name,
                    null, element.MemberProperty, null);
                return true;
            }
            return false;
        }

        if (tag.IsClosing) return false;
        foreach (var attribute in tag.Attributes)
        {
            if (tag.Kind == SilkyUIXmlTagKind.Body && attribute.ValueStart >= 0 && attribute.ValueComplete &&
                position >= attribute.ValueStart && position < attribute.ValueStart + attribute.Value.Length &&
                SilkyUIXmlSyntax.GetSuiAttributeKind(tag.Scope, attribute.Name) == SilkyUIAttributeKind.Class)
            {
                var bodyClass = element.BodyClass;
                if (bodyClass == null) return false;
                symbol = new(SilkyUISymbolKind.BodyClass, attribute.ValueStart, attribute.Value.Length,
                    attribute.Value, tag.Name, null, null, bodyClass);
                return true;
            }

            if (position < attribute.NameStart || position >= attribute.NameEnd) continue;
            if (attribute.ValueStart < 0) return false;
            var resolved = ResolveAttribute(tag, attribute);
            if (resolved.Kind is not (SilkyUISemanticAttributeKind.Property or SilkyUISemanticAttributeKind.Binding) ||
                resolved.Property == null ||
                resolved.Property.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                return false;
            symbol = new(SilkyUISymbolKind.Attribute, attribute.NameStart, attribute.Name.Length,
                attribute.Name, tag.Name, element.MappingClass, resolved.Property, element.BodyClass);
            return true;
        }
        return false;
    }

    private SilkyUIElementInfo ResolveBody(SilkyUIXmlTag tag)
    {
        tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className);
        SilkyUIElementGroupClass bodyClass = string.IsNullOrEmpty(Document.FilePath)
            ? Metadata.GetGroupClassByName(className) : null;
        if (!string.IsNullOrWhiteSpace(className) &&
            ClrProject?.Compilation.Assembly.GetTypeByMetadataName(className) is { } type)
        {
            var line = type.Locations.FirstOrDefault(location => location.IsInSource)?.GetLineSpan();
            bodyClass = new(type.Name, type.ToDisplayString(), [.. GetReadableProperties(type)],
                line?.Path ?? string.Empty, line?.StartLinePosition.Line ?? 0, line?.StartLinePosition.Character ?? 0);
        }
        return new(tag, true, null, bodyClass, bodyClass == null ? [] : [.. bodyClass.Properties], null);
    }

    private SilkyUIElementInfo ResolveOrdinary(SilkyUIXmlTag tag)
    {
        var prefix = SilkyUIXmlSyntax.GetPrefix(tag.Name);
        var uri = tag.Scope?.Resolve(prefix) ?? string.Empty;
        XmlMappingClass mapped;
        if (SilkyUIClrNamespace.IsClrNamespace(uri))
        {
            if (ClrProject == null)
                return new(tag, false, null, null, [], null, "无法确定此 XML 所属的 C# 项目，或项目元数据尚未就绪");
            var type = ClrProject.ResolveType(uri, SilkyUIXmlSyntax.GetLocalName(tag.Name), ClrAccessContext, out var error);
            if (type == null) return new(tag, false, null, null, [], null, error);
            var line = type.Locations.FirstOrDefault(location => location.IsInSource)?.GetLineSpan();
            mapped = new(type, [.. GetReadableProperties(type)], tag.Name, line?.Path ?? string.Empty,
                line?.StartLinePosition.Line ?? 0, line?.StartLinePosition.Character ?? 0);
        }
        else
        {
            if (uri.Length > 0 || prefix.Length > 0)
                return new(tag, false, null, null, [], null, uri.Length > 0
                    ? $"不支持元素命名空间 '{uri}'" : $"未声明命名空间前缀 '{prefix}'");
            mapped = Metadata.GetClassByName(tag.Name);
        }
        return new(tag, mapped != null, mapped, null, mapped == null ? [] : [.. mapped.Properties], null);
    }

    private SilkyUIElementInfo ResolveMember(SilkyUIXmlTag tag)
    {
        var member = FindProperty(GetParentProperties(tag), SilkyUIXmlSyntax.GetLocalName(tag.Name));
        if (member == null || !IsExpandableMemberProperty(member.Property))
            return new(tag, false, null, null, [], null);
        return new(tag, true, null, null, GetReadableProperties(member.Property.Type), member);
    }

    private ImmutableList<SilkyUIProperty> ResolveStyleProperties(SilkyUIXmlTag tag)
        => tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Target, out var target)
            ? Metadata.GetTargetProperties(target)
            : Metadata.StyleProperties;

    private static SilkyUIProperty FindProperty(IEnumerable<SilkyUIProperty> properties, string name)
        => string.IsNullOrEmpty(name) ? null : properties.FirstOrDefault(item => item.Property.Name == name);

    private static bool IsExpandableMemberProperty(IPropertySymbol property)
        => property != null && !property.IsStatic && !property.IsIndexer && property.Parameters.Length == 0 &&
           property.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
           property.Type is INamedTypeSymbol type && type.SpecialType == SpecialType.None &&
           type.TypeKind is TypeKind.Class or TypeKind.Interface;

    private static bool IsUIViewType(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.ToDisplayString() == "SilkyUIFramework.Elements.UIView") return true;
        return false;
    }

    /// <summary>保留可读属性供下一层成员展开；赋值与绑定由各功能检查公开 setter。</summary>
    private static ImmutableList<SilkyUIProperty> GetReadableProperties(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType) return [];
        var properties = new List<SilkyUIProperty>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in GetPropertyTypes(namedType))
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                if (seen.Add(property.Name) && !property.IsStatic && !property.IsIndexer && property.Parameters.Length == 0 &&
                    property.DeclaredAccessibility == Accessibility.Public &&
                    property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
                    properties.Add(CreateProperty(property));
        return [.. properties];
    }

    private static IEnumerable<INamedTypeSymbol> GetPropertyTypes(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
            yield return current;

        // 接口通过继承接口提供属性；类仍按自身及基类查找，避免暴露显式接口实现。
        if (type.TypeKind == TypeKind.Interface)
            foreach (var inherited in type.AllInterfaces)
                yield return inherited;
    }

    private static SilkyUIProperty CreateProperty(IPropertySymbol property)
    {
        var location = property.Locations.FirstOrDefault(item => item.IsInSource);
        var lineSpan = location?.GetLineSpan();
        ImmutableArray<string> enums = property.Type is not INamedTypeSymbol type || type.TypeKind != TypeKind.Enum
            ? []
            : [.. type.GetMembers().OfType<IFieldSymbol>().Where(field => field.IsStatic && field.IsConst &&
                field.DeclaredAccessibility == Accessibility.Public).Select(field => field.Name)];
        return new(property, enums, lineSpan?.Path ?? string.Empty,
            lineSpan?.StartLinePosition.Line ?? 0, lineSpan?.StartLinePosition.Character ?? 0);
    }
}
