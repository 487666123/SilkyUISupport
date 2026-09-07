using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

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
        if (context.TagStart < 0) return null;

        if (context.Tag == null || !context.Tag.TryGetSuiAttributeValue(
                SilkyUIAttributeKind.Class, out var className) ||
            string.IsNullOrWhiteSpace(className))
            return null;

        return m_metadataService.GetAllGroupClasses().FirstOrDefault(c => c.FullName == className)
            ?? m_metadataService.GetAllGroupClasses().FirstOrDefault(c => c.Name == className);
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

    private static void AddBindingPropertyCompletions(
        ICollection<Completion> completions, string prefix, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            if (property.Property.SetMethod == null)
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
                break;
            }
            case XmlContextType.AttributeName:
            {
                if (tag == null) break;
                if (tag.Kind == SilkyUIXmlTagKind.Style)
                {
                    foreach (var suiPrefix in suiPrefixes)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Name", "定义样式名称");
                    break;
                }
                if (tag.Kind is not (SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary)) break;

                var properties = tag.Kind == SilkyUIXmlTagKind.Body
                    ? ResolveBodyClass(context)?.Properties ?? []
                    : m_metadataService.GetClassByName(currentTag)?.Properties ?? [];
                var attributePrefix = SilkyUIXmlSyntax.GetPrefix(context.CurrentAttribute);
                var hasColon = context.CurrentAttribute.IndexOf(':') >= 0;
                if (hasColon && bindingPrefixes.Contains(attributePrefix, StringComparer.Ordinal))
                {
                    AddBindingPropertyCompletions(m_compList, attributePrefix, properties);
                    break;
                }

                // Keep ordinary properties available while a namespace prefix is only partially typed.
                if (!hasColon)
                {
                    AddOrdinaryPropertyCompletions(m_compList, properties);
                    foreach (var bindingPrefix in bindingPrefixes)
                        AddBindingPropertyCompletions(m_compList, bindingPrefix, properties);
                }

                foreach (var suiPrefix in suiPrefixes)
                {
                    if (hasColon && attributePrefix != suiPrefix) continue;
                    if (tag.Kind == SilkyUIXmlTagKind.Body)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Class", "指定 UIElementGroup 子类全名");
                    else if (m_metadataService.GetClassByName(currentTag) != null)
                        AddDirectiveCompletion(m_compList, suiPrefix, "Name", "生成 C# 控件属性");
                    else
                        continue;
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

                if (tag == null || tag.Kind is not (SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary)) break;

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

                SilkyUIProperty property = null;
                if (currentTag == "Body")
                {
                    property = ResolveBodyClass(context)?.Properties
                        .FirstOrDefault(item => item.Property.Name == context.CurrentAttribute);
                }
                else
                {
                    property = m_metadataService.GetPropertyByName(currentTag, context.CurrentAttribute);
                }

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

