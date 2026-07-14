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

    /// <summary>从 Body 标签中解析 Class 属性值，查找对应的 UIElementGroup 类。</summary>
    private SilkyUIElementGroupClass ResolveBodyClass(ICompletionSession session)
    {
        var point = session.TextView.Caret.Position.BufferPosition;
        var snapshot = point.Snapshot;
        var text = snapshot.GetText();
        int pos = point.Position;

        // 从光标向前找 <Body
        int tagStart = text.LastIndexOf("<Body", pos, StringComparison.OrdinalIgnoreCase);
        if (tagStart < 0) return null;

        // 取标签起始到光标位置的内容
        int tagEnd = text.IndexOf('>', tagStart);
        int searchEnd = tagEnd < 0 ? pos : Math.Min(pos, tagEnd);
        if (searchEnd <= tagStart) return null;

        var tagSection = text.Substring(tagStart, searchEnd - tagStart);

        // 找 Class="..."
        int classIdx = tagSection.IndexOf("Class=", StringComparison.OrdinalIgnoreCase);
        if (classIdx < 0) return null;

        int valStart = classIdx + 6;
        if (valStart >= tagSection.Length) return null;
        if (tagSection[valStart] != '"' && tagSection[valStart] != '\'') return null;
        char quote = tagSection[valStart];
        valStart++;

        int valEnd = tagSection.IndexOf(quote, valStart);
        if (valEnd < 0) return null;

        var className = tagSection.Substring(valStart, valEnd - valStart);
        if (string.IsNullOrWhiteSpace(className)) return null;

        // 在 UIElementGroup 类中按全名查找，再按短名查找
        return m_metadataService.GetAllGroupClasses().FirstOrDefault(c => c.FullName == className)
            ?? m_metadataService.GetAllGroupClasses().FirstOrDefault(c => c.Name == className);
    }

    /*
     * 这个方法是ICompletionSource接口的核心实现
     * 当补全弹窗要显示内容时，VS会自动调用这个方法，让我们把补全项添加到completionSets里
     */
    void ICompletionSource.AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        m_compList.Clear();

        // 分析当前上下文
        var context = XmlContextAnalyzer.Analyze(session);

        switch (context.ContextType)
        {
            case XmlContextType.TagName:
            {
                // 标签名位置：框架根元素 + 映射类名
                m_compList.Add(new Completion4("Body", "Body", "根元素", KnownMonikers.Class));

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
                // 属性名位置：显示当前标签的所有属性
                if (context.CurrentTag == "Body")
                {
                    m_compList.Add(new Completion4("Class", "Class", "指定 UIElementGroup 子类全名", KnownMonikers.Property));

                    // 从标签中解析 Class 属性值，获取对应类的属性补全
                    if (ResolveBodyClass(session) is { } bodyClass)
                    {
                        foreach (var property in bodyClass.Properties)
                        {
                            var propertyName = property.Property.Name;
                            var propertyTypeName = property.Property.Type.ToDisplayString();
                            m_compList.Add(new Completion4(
                                propertyName,
                                propertyName,
                                propertyTypeName,
                                KnownMonikers.Property,
                                suffix: propertyTypeName));
                        }
                    }
                    break;
                }

                var tagClass = m_metadataService.GetClassByName(context.CurrentTag);
                if (tagClass != null)
                {
                    foreach (var property in tagClass.Properties)
                    {
                        var propertyName = property.Property.Name;
                        var propertyTypeName = property.Property.Type.ToDisplayString();
                        m_compList.Add(new Completion4(
                            propertyName,
                            propertyName,
                            propertyTypeName,
                            KnownMonikers.Property,
                            suffix: propertyTypeName));
                    }
                }
                break;
            }
            case XmlContextType.AttributeValue:
            {
                // Body Class 属性值位置：显示所有 UIElementGroup 子类的全名
                if (context.CurrentTag == "Body" && context.CurrentAttribute == "Class")
                {
                    if (context.CurrentAttribute == "Class")
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
                    }
                    else
                    {
                        if (ResolveBodyClass(session) is { } bodyClass)
                        {
                            var prop = bodyClass.Properties.FirstOrDefault(p => p.Property.Name == context.CurrentAttribute);
                            if (prop != null && prop.Enums.Any())
                            {
                                foreach (var @enum in prop.Enums)
                                {
                                    m_compList.Add(new Completion4(@enum, @enum, @enum, KnownMonikers.Enumeration));
                                }
                            }
                        }
                    }

                    break;
                }

                // 其他属性值位置：只显示枚举值
                var property = m_metadataService.GetPropertyByName(context.CurrentTag, context.CurrentAttribute);
                if (property != null && property.Enums.Any())
                {
                    foreach (var @enum in property.Enums)
                    {
                        m_compList.Add(new Completion4(@enum, @enum, @enum, KnownMonikers.Enumeration));
                    }
                }
                break;
            }
        }

        // 如果有补全项才添加到补全集合
        if (m_compList.Any())
        {
            if (completionSets.Count > 0)
            {
                foreach (var item in completionSets[0].Completions)
                {
                    m_compList.Add(item);
                }
                completionSets.RemoveAt(0);
            }

            completionSets.Insert(0, new SilkyUICompletionSet(
                "SilkyUI",
                "SilkyUI",
                FindTokenSpanAtPosition(session),
                m_compList));
        }

        return;
    }

    /*
     * 查找当前光标所在位置的单词范围
     * 作用是：当用户选中补全项时，知道要把编辑器里的哪些文本替换成补全内容
     * 比如用户输入了"add"，这时补全里有"addition"，选中后就会把"add"替换成"addition"
     */
    private ITrackingSpan FindTokenSpanAtPosition(ICompletionSession session)
    {
        var currentPoint = session.TextView.Caret.Position.BufferPosition;
        var snapshot = currentPoint.Snapshot;

        var start = currentPoint.Position;
        var end = currentPoint.Position;

        // 向前查找：找到标签起始位置 <，直到遇到分隔符
        while (start > 0)
        {
            char c = snapshot[start - 1];
            // XML标签名允许的字符：字母、数字、.、_、-、:
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-' && c != ':')
                break;
            start--;
        }

        // 向后查找：找到标签结束位置
        while (end < snapshot.Length)
        {
            char c = snapshot[end];
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-' && c != ':')
                break;
            end++;
        }

        return snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeInclusive);
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

