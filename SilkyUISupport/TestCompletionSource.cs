using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Name("token completion")]
[Export(typeof(ICompletionSourceProvider))]
[ContentType("xml")]
internal class TestCompletionSourceProvider : ICompletionSourceProvider
{
    [Import]
    public ITextStructureNavigatorSelectorService NavigatorService { get; set; }

    [Import]
    public IGlyphService GlyphService { get; set; }

    [Import]
    public AttributeClassScanner ClassScanner { get; set; }

    ICompletionSource ICompletionSourceProvider.TryCreateCompletionSource(ITextBuffer textBuffer) => new TestCompletionSource(this, textBuffer);
}

/*
 * 【初学者注释】
 * 这个类是补全内容的提供者，实现了ICompletionSource接口
 * 当补全弹窗要显示内容时，会调用这个类的方法来获取补全列表
 * 你可以在这里自定义你想要显示的补全项，比如关键字、自定义代码片段等
 */
internal class TestCompletionSource : ICompletionSource
{
    private readonly TestCompletionSourceProvider m_sourceProvider;
    private readonly ITextBuffer m_textBuffer; // 当前的文本缓冲区，也就是编辑器里的内容

    private readonly List<Completion> m_compList = [];

    // 这里替换成你想要扫描的特性名称，比如要找[SilkyUIElement]特性的类，就写"SilkyUIElement"
    private const string TargetAttributeName = "SilkyUIFramework.Attributes.XmlElementMappingAttribute";

    public TestCompletionSource(TestCompletionSourceProvider sourceProvider, ITextBuffer textBuffer)
    {
        m_sourceProvider = sourceProvider;
        m_textBuffer = textBuffer;
    }

    /*
     * 这个方法是ICompletionSource接口的核心实现
     * 当补全弹窗要显示内容时，VS会自动调用这个方法，让我们把补全项添加到completionSets里
     */
    void ICompletionSource.AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        var classIcon = m_sourceProvider.GlyphService.GetGlyph(StandardGlyphGroup.GlyphGroupClass, StandardGlyphItem.GlyphItemPublic);
        var propertyIcon = m_sourceProvider.GlyphService.GetGlyph(StandardGlyphGroup.GlyphGroupProperty, StandardGlyphItem.GlyphItemPublic);
        var enumIcon = m_sourceProvider.GlyphService.GetGlyph(StandardGlyphGroup.GlyphGroupEnum, StandardGlyphItem.GlyphItemPublic);

        m_compList.Clear();

        // 动态获取带目标特性的类名
        var suiClasses = m_sourceProvider.ClassScanner.GetClassesWithAttribute(TargetAttributeName);
        foreach (var suiClass in suiClasses)
        {
            m_compList.Add(new Completion(suiClass.Name, suiClass.Name, suiClass.FullName, classIcon, null));

            foreach (var property in suiClass.Properties)
            {
                m_compList.Add(new Completion(property.Name, property.Name, property.Name, propertyIcon, null));

                foreach (var @enum in property.Enums)
                {
                    m_compList.Add(new Completion(@enum, @enum, @enum, propertyIcon, null));
                }
            }
        }

        completionSets.Insert(0, new CompletionSet(
            "SilkyUI",
            "SilkyUI CompletionSet",
            FindTokenSpanAtPosition(session.GetTriggerPoint(m_textBuffer), session),
            m_compList,
            null));

        return;
    }

    /*
     * 查找当前光标所在位置的单词范围
     * 作用是：当用户选中补全项时，知道要把编辑器里的哪些文本替换成补全内容
     * 比如用户输入了"add"，这时补全里有"addition"，选中后就会把"add"替换成"addition"
     */
    private ITrackingSpan FindTokenSpanAtPosition(ITrackingPoint point, ICompletionSession session)
    {
        SnapshotPoint currentPoint = session.TextView.Caret.Position.BufferPosition;
        ITextSnapshot snapshot = currentPoint.Snapshot;
        int start = currentPoint.Position;
        int end = currentPoint.Position;

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
