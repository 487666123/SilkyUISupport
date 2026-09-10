using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Microsoft.CodeAnalysis;

namespace SilkyUISupport;

[Name("SilkyUI XML completion source")]
[Export(typeof(ICompletionSourceProvider))]
[ContentType("SilkyUI XML")]
internal class SilkyUICompletionSourceProvider : ICompletionSourceProvider
{
    [Import]
    public IGlyphService GlyphService { get; set; }

    [Import]
    public SilkyUIMetadataService MetadataService { get; set; } = null;

    ICompletionSource ICompletionSourceProvider.TryCreateCompletionSource(ITextBuffer textBuffer) => new SilkyUICompletionSource(this, textBuffer, MetadataService);
}

/*
 * 【初学者注释】
 * 这个类是补全内容的提供者，实现了ICompletionSource接口
 * 当补全弹窗要显示内容时，会调用这个类的方法来获取补全列表
 * 你可以在这里自定义你想要显示的补全项，比如关键字、自定义代码片段等
 */
internal class SilkyUICompletionSource(SilkyUICompletionSourceProvider sourceProvider, ITextBuffer textBuffer, SilkyUIMetadataService metadataService) : ICompletionSource
{
    private readonly SilkyUICompletionSourceProvider m_sourceProvider = sourceProvider;
    private readonly ITextBuffer m_textBuffer = textBuffer;
    private readonly SilkyUIMetadataService m_metadataService = metadataService;

    private readonly List<Completion> m_compList = [];

    /// <summary>从根元素的 sui:Class 属性解析对应的 UIElementGroup 类。</summary>
    private SilkyUIElementGroupClass ResolveBodyClass(XmlContext context)
    {
        return context?.TagStart >= 0 ? ResolveBodyClass(context.Tag) : null;
    }

    private SilkyUIElementGroupClass ResolveBodyClass(SilkyUIXmlTag tag)
    {
        if (tag == null || !tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out var className) ||
            string.IsNullOrWhiteSpace(className))
            return null;

        var groupClasses = m_metadataService.GetAllGroupClasses();
        return groupClasses.FirstOrDefault(c => c.FullName == className)
            ?? groupClasses.FirstOrDefault(c => c.Name == className);
    }

    private IEnumerable<SilkyUIProperty> ResolvePropertiesForTag(
        SilkyUIXmlDocument document, SilkyUIXmlTag tag)
    {
        if (tag == null) return null;

        if (tag.Kind == SilkyUIXmlTagKind.Body)
            return ResolveBodyClass(document.GetBodyTag())?.Properties;

        if (tag.Kind == SilkyUIXmlTagKind.Ordinary)
            return m_metadataService.GetClassByName(tag.Name)?.Properties;

        // 当前只支持直接挂在 Body 或普通元素下的 M.Xxx。
        return null;
    }

    private INamedTypeSymbol ResolveMemberType(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag)
    {
        if (memberTag == null || memberTag.Kind != SilkyUIXmlTagKind.Member ||
            memberTag.Name.Length <= 2)
            return null;

        var parentTag = document.GetParentTag(memberTag.Start);
        var parentProperties = ResolvePropertiesForTag(document, parentTag);
        if (parentProperties == null) return null;

        var memberName = memberTag.Name.Substring(2);
        var memberProperty = parentProperties.FirstOrDefault(property =>
            property.Property.Name == memberName);
        return memberProperty?.Property.Type as INamedTypeSymbol;
    }

    private IEnumerable<SilkyUIProperty> ResolveMemberProperties(
        SilkyUIXmlDocument document, SilkyUIXmlTag memberTag)
    {
        return ResolveMemberType(document, memberTag) is { } memberType
            ? GetNamedTypeProperties(memberType)
            : null;
    }

    private IEnumerable<SilkyUIProperty> ResolveStyleProperties(SilkyUIXmlTag styleTag)
    {
        if (styleTag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Target, out var targetName))
            return m_metadataService.GetTargetProperties(targetName);

        return m_metadataService.GetAllStyleProperties();
    }

    private static IEnumerable<SilkyUIProperty> GetNamedTypeProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!seen.Add(property.Name) || property.IsStatic ||
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                    continue;

                yield return new SilkyUIProperty(
                    property,
                    GetEnumValues(property.Type),
                    string.Empty,
                    0,
                    0);
            }
        }
    }

    private static ImmutableArray<string> GetEnumValues(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol enumType || type.TypeKind != TypeKind.Enum)
            return [];

        return [.. enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field.IsStatic && field.IsConst &&
                            field.DeclaredAccessibility == Accessibility.Public)
            .Select(field => field.Name)];
    }

    private static bool IsSuiAttribute(XmlContext context, SilkyUIAttributeKind kind)
    {
        return SilkyUIXmlSyntax.GetSuiAttributeKind(context.Tag?.Scope, context.CurrentAttribute) == kind;
    }

    private static void AddDirectiveCompletion(
        ICollection<Completion> completions, string prefix, string localName, string description)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        completions.Add(new Completion4(
            $"{prefix}:{localName}",
            $"{prefix}:{localName}",
            description,
            KnownMonikers.Property));
    }

    private static void AddOrdinaryPropertyCompletions(
        ICollection<Completion> completions, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            if (property.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                continue;

            var propertyName = property.Property.Name;
            var propertyTypeName = property.Property.Type.ToDisplayString();
            completions.Add(new Completion4(
                propertyName,
                propertyName,
                propertyTypeName,
                KnownMonikers.Property,
                suffix: propertyTypeName));
        }
    }

    private static void AddStyleNameCompletions(
        ICollection<Completion> completions, ITextSnapshot snapshot)
    {
        foreach (var styleName in SilkyUIXmlDocument.Get(snapshot).StyleNames)
        {
            completions.Add(new Completion4(
                styleName,
                styleName,
                "样式名称",
                KnownMonikers.Property));
        }
    }

    private static void AddEnumValueCompletions(
        ICollection<Completion> completions, SilkyUIProperty property)
    {
        if (property == null) return;
        foreach (var @enum in property.Enums)
            completions.Add(new Completion4(@enum, @enum, @enum, KnownMonikers.Enumeration));
    }

    private static void AddTargetClassCompletions(
        ICollection<Completion> completions, IEnumerable<SilkyUITargetClass> targetClasses)
    {
        foreach (var targetClass in targetClasses)
        {
            completions.Add(new Completion4(
                targetClass.Class.Name,
                targetClass.FullName,
                targetClass.FullName,
                KnownMonikers.Class,
                suffix: targetClass.FullName));
        }
    }

    private static void AddStylePropertyCompletions(
        ICollection<Completion> completions, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            var propertyName = property.Property.Name;
            var propertyTypeName = property.Property.Type.ToDisplayString();
            completions.Add(new Completion4(
                propertyName,
                propertyName,
                propertyTypeName,
                KnownMonikers.Property,
                suffix: propertyTypeName));
        }
    }

    private static bool IsUIViewType(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.ToDisplayString() == "SilkyUIFramework.Elements.UIView")
                return true;

        return false;
    }

    private bool CanBindMember(SilkyUIXmlDocument document, SilkyUIXmlTag memberTag)
        => IsUIViewType(ResolveMemberType(document, memberTag));

    private static void AddBindingPropertyCompletions(
        ICollection<Completion> completions, string prefix, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            if (property.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                continue;

            var propertyName = property.Property.Name;
            var propertyTypeName = property.Property.Type.ToDisplayString();
            completions.Add(new Completion4(
                $"{prefix}:{propertyName}",
                $"{prefix}:{propertyName}",
                propertyTypeName,
                KnownMonikers.Property,
                suffix: propertyTypeName));
        }
    }

    private static bool IsExpandableMemberProperty(IPropertySymbol property)
    {
        if (property.IsStatic ||
            property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
            property.Type is not INamedTypeSymbol type ||
            type.SpecialType != SpecialType.None ||
            type.TypeKind is not (TypeKind.Class or TypeKind.Interface))
            return false;

        return true;
    }

    private static void AddMemberPropertyCompletions(
        ICollection<Completion> completions, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            // M.* 展开现有引用对象；父属性只需可读，不要求 Setter。
            if (!IsExpandableMemberProperty(property.Property))
                continue;

            var propertyName = property.Property.Name;
            var propertyTypeName = property.Property.Type.ToDisplayString();
            completions.Add(new Completion4(
                $"M.{propertyName}",
                $"M.{propertyName}",
                propertyTypeName,
                KnownMonikers.Property,
                suffix: propertyTypeName));
        }
    }

    /*
     * 这个方法是ICompletionSource接口的核心实现
     * 当补全弹窗要显示内容时，VS会自动调用这个方法，让我们把补全项添加到completionSets里
     */
    void ICompletionSource.AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        m_compList.Clear();

        var context = XmlContextAnalyzer.Analyze(session);
        var snapshot = session.TextView.Caret.Position.BufferPosition.Snapshot;
        var currentTag = context.CurrentTag;
        var tag = context.Tag;
        var suiPrefixes = tag?.Scope.GetPrefixes(SilkyUIXmlSyntax.NamespaceUri).ToArray() ?? [];
        var bindingPrefixes = tag?.Scope.GetPrefixes(SilkyUIXmlSyntax.BindingNamespaceUri).ToArray() ?? [];

        switch (context.ContextType)
        {
            case XmlContextType.TagName:
            {
                var isMemberTag = currentTag == "M" ||
                                  currentTag.StartsWith("M.", StringComparison.Ordinal);
                if (isMemberTag)
                {
                    var document = SilkyUIXmlDocument.Get(snapshot);
                    var parentTag = document.GetParentTag(context.TagStart);
                    var parentProperties = ResolvePropertiesForTag(document, parentTag);
                    AddMemberPropertyCompletions(m_compList, parentProperties);
                }
                else
                {
                    m_compList.Add(new Completion4("Body", "Body", "根元素", KnownMonikers.Class));
                    foreach (var suiPrefix in suiPrefixes)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Style", "SilkyUI 样式元素");

                    foreach (var xmlMappingClass in m_metadataService.GetAllClasses())
                    {
                        m_compList.Add(new Completion4(
                            xmlMappingClass.Alias,
                            xmlMappingClass.Alias,
                            xmlMappingClass.Class.ToDisplayString(),
                            KnownMonikers.Class,
                            suffix: xmlMappingClass.Class.ToDisplayString()));
                    }
                }
                break;
            }
            case XmlContextType.AttributeName:
            {
                if (tag == null) break;
                if (tag.Kind == SilkyUIXmlTagKind.Style)
                {
                    AddStylePropertyCompletions(m_compList, ResolveStyleProperties(tag));
                    foreach (var suiPrefix in suiPrefixes)
                    {
                        AddDirectiveCompletion(m_compList, suiPrefix, "Name", "定义样式名称");
                        AddDirectiveCompletion(m_compList, suiPrefix, "Target", "指定样式属性来源类全名");
                    }
                    break;
                }

                var document = SilkyUIXmlDocument.Get(snapshot);
                var properties = tag.Kind == SilkyUIXmlTagKind.Member
                    ? ResolveMemberProperties(document, tag)
                    : ResolvePropertiesForTag(document, tag);
                if (properties == null) break;

                var canBind = tag.Kind != SilkyUIXmlTagKind.Member || CanBindMember(document, tag);
                var attributePrefix = SilkyUIXmlSyntax.GetPrefix(context.CurrentAttribute);
                var hasColon = context.CurrentAttribute.IndexOf(':') >= 0;
                if (canBind && hasColon && bindingPrefixes.Contains(attributePrefix, StringComparer.Ordinal))
                {
                    AddBindingPropertyCompletions(m_compList, attributePrefix, properties);
                    break;
                }

                // Keep ordinary properties available while a namespace prefix is only partially typed.
                if (!hasColon)
                {
                    AddOrdinaryPropertyCompletions(m_compList, properties);
                    if (canBind)
                    {
                        foreach (var bindingPrefix in bindingPrefixes)
                            AddBindingPropertyCompletions(m_compList, bindingPrefix, properties);
                    }
                }

                foreach (var suiPrefix in suiPrefixes)
                {
                    if (hasColon && attributePrefix != suiPrefix) continue;

                    if (tag.Kind == SilkyUIXmlTagKind.Body)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Class", "指定 UIElementGroup 子类全名");
                    else if (tag.Kind == SilkyUIXmlTagKind.Ordinary)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Name", "生成 C# 控件属性");

                    if (tag.Kind is SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary or SilkyUIXmlTagKind.Member)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Style", "引用一个或多个样式");
                }
                break;
            }
            case XmlContextType.AttributeValue:
            {
                if (SilkyUIXmlSyntax.IsNamespaceDeclaration(context.CurrentAttribute))
                {
                    m_compList.Add(new Completion4(
                        SilkyUIXmlSyntax.NamespaceUri,
                        SilkyUIXmlSyntax.NamespaceUri,
                        "SilkyUIFramework 命名空间 URI",
                        KnownMonikers.Property));
                    m_compList.Add(new Completion4(
                        SilkyUIXmlSyntax.BindingNamespaceUri,
                        SilkyUIXmlSyntax.BindingNamespaceUri,
                        "SilkyUIFramework 绑定命名空间 URI",
                        KnownMonikers.Property));
                    break;
                }

                if (tag == null) break;

                if (tag.Kind == SilkyUIXmlTagKind.Style && IsSuiAttribute(context, SilkyUIAttributeKind.Target))
                {
                    AddTargetClassCompletions(m_compList, m_metadataService.GetAllTargetClasses());
                    break;
                }

                if (tag.Kind == SilkyUIXmlTagKind.Body && IsSuiAttribute(context, SilkyUIAttributeKind.Class))
                {
                    foreach (var uiClass in m_metadataService.GetAllGroupClasses())
                    {
                        m_compList.Add(new Completion4(
                            uiClass.Name,
                            uiClass.FullName,
                            uiClass.FullName,
                            KnownMonikers.Class,
                            suffix: uiClass.FullName));
                    }
                    break;
                }

                if (IsSuiAttribute(context, SilkyUIAttributeKind.Style))
                {
                    AddStyleNameCompletions(m_compList, snapshot);
                    break;
                }

                if (context.CurrentAttribute.Contains(":", StringComparison.Ordinal))
                    break;

                var document = SilkyUIXmlDocument.Get(snapshot);
                var properties = tag.Kind == SilkyUIXmlTagKind.Member
                    ? ResolveMemberProperties(document, tag)
                    : ResolvePropertiesForTag(document, tag);
                var property = properties?.FirstOrDefault(item =>
                    item.Property.Name == context.CurrentAttribute &&
                    item.Property.SetMethod?.DeclaredAccessibility == Accessibility.Public);

                AddEnumValueCompletions(m_compList, property);
                break;
            }
        }

        if (m_compList.Any())
        {
            if (completionSets.Count > 0)
            {
                foreach (var item in completionSets[0].Completions)
                    m_compList.Add(item);
                completionSets.RemoveAt(0);
            }

            completionSets.Insert(0, new SilkyUICompletionSet(
                "SilkyUI",
                "SilkyUI",
                FindTokenSpanAtPosition(session, context),
                m_compList));
        }
    }

    /*
     * 查找当前光标所在位置的单词范围
     * 作用是：当用户选中补全项时，知道要把编辑器里的哪些文本替换成补全内容
     * 比如用户输入了"add"，这时补全里有"addition"，选中后就会把"add"替换成"addition"
     */
    private ITrackingSpan FindTokenSpanAtPosition(
        ICompletionSession session, XmlContext context)
    {
        var currentPoint = session.TextView.Caret.Position.BufferPosition;
        var snapshot = currentPoint.Snapshot;
        var isNamespaceUri = context.ContextType == XmlContextType.AttributeValue &&
                             SilkyUIXmlSyntax.IsNamespaceDeclaration(context.CurrentAttribute);

        var start = currentPoint.Position;
        var end = currentPoint.Position;

        while (start > 0 && IsCompletionCharacter(snapshot[start - 1], isNamespaceUri))
            start--;

        while (end < snapshot.Length && IsCompletionCharacter(snapshot[end], isNamespaceUri))
            end++;

        return snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeInclusive);
    }

    private static bool IsCompletionCharacter(char value, bool isNamespaceUri)
    {
        if (char.IsLetterOrDigit(value))
            return true;

        return isNamespaceUri
            ? value is ':' or '/' or '.' or '-' or '_' or '~' or '?' or '&' or '=' or '%' or '#'
            : value is ':' or '.' or '_' or '-';
    }


    private bool m_isDisposed;

    public void Dispose()
    {
        if (!m_isDisposed)
        {
            // 告诉垃圾回收器不需要调用这个对象的析构函数了
            GC.SuppressFinalize(this);
            m_isDisposed = true;
        }
    }
}

