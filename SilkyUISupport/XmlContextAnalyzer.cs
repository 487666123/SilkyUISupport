using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

/// <summary>
/// XML 上下文类型
/// </summary>
internal enum XmlContextType
{
    /// <summary>
    /// 未知上下文
    /// </summary>
    Unknown,
    /// <summary>
    /// 标签名位置（<xxx 中的 xxx）
    /// </summary>
    TagName,
    /// <summary>
    /// 属性名位置（<tag xxx= 中的 xxx）
    /// </summary>
    AttributeName,
    /// <summary>
    /// 属性值位置（<tag attr="xxx" 中的 xxx）
    /// </summary>
    AttributeValue
}

/// <summary>
/// XML 上下文信息
/// </summary>
internal class XmlContext
{
    public XmlContextType ContextType { get; set; }
    public string CurrentTag { get; set; } = string.Empty;
    public string CurrentAttribute { get; set; } = string.Empty;
}

/// <summary>
/// XML 上下文分析器（公共服务，供所有功能使用）
/// </summary>
internal static class XmlContextAnalyzer
{
    /// <summary>
    /// 分析当前光标位置的 XML 上下文（补全场景）
    /// </summary>
    public static XmlContext Analyze(ICompletionSession session)
    {
        var currentPoint = session.TextView.Caret.Position.BufferPosition;
        return Analyze(currentPoint.Snapshot, currentPoint.Position);
    }

    /// <summary>
    /// 分析当前光标位置的 XML 上下文（通用场景）
    /// </summary>
    public static XmlContext Analyze(ITextSnapshot snapshot, int position)
    {
        var context = new XmlContext();

        if (position == 0 || position >= snapshot.Length)
            return context;

        // 获取当前行的文本
        var line = snapshot.GetLineFromPosition(position);
        var lineText = line.GetText();
        var linePosition = position - line.Start.Position;

        // 查找标签起始位置 '<'（支持跨多行，最多向上扫描10行）
        int tagStartLine = line.LineNumber;
        int tagStartCharPosition = -1;
        int currentScanLineNumber = line.LineNumber;
        const int maxScanLines = 10;

        while (currentScanLineNumber >= 0 && currentScanLineNumber >= tagStartLine - maxScanLines)
        {
            var currentScanLine = snapshot.GetLineFromLineNumber(currentScanLineNumber);
            string currentScanText = currentScanLine.GetText();
            int scanEndPosition = (currentScanLineNumber == line.LineNumber) ? linePosition : currentScanText.Length;

            // 从后往前查找
            for (int i = scanEndPosition - 1; i >= 0; i--)
            {
                if (currentScanText[i] == '>')
                {
                    // 遇到标签结束符，前面没有有效起始标签了
                    return context;
                }
                if (currentScanText[i] == '<')
                {
                    tagStartLine = currentScanLineNumber;
                    tagStartCharPosition = i;
                    goto FoundTagStart;
                }
            }

            currentScanLineNumber--;
        }

        FoundTagStart:
        if (tagStartCharPosition == -1)
            return context;

        // 拼接从标签起始到光标位置的所有内容（跨多行）
        var tagContentBuilder = new StringBuilder();
        for (int i = tagStartLine; i <= line.LineNumber; i++)
        {
            var currentLine = snapshot.GetLineFromLineNumber(i);
            string currentLineText = currentLine.GetText();

            if (i == tagStartLine && i == line.LineNumber)
            {
                // 标签和光标在同一行
                tagContentBuilder.Append(currentLineText.Substring(tagStartCharPosition, linePosition - tagStartCharPosition));
            }
            else if (i == tagStartLine)
            {
                // 标签起始行：从'<'到行尾
                tagContentBuilder.Append(currentLineText.Substring(tagStartCharPosition));
                tagContentBuilder.Append(' '); // 换行换成空格，不影响XML结构分析
            }
            else if (i == line.LineNumber)
            {
                // 当前行：从行首到光标位置
                tagContentBuilder.Append(currentLineText.Substring(0, linePosition));
            }
            else
            {
                // 中间行：全部内容
                tagContentBuilder.Append(currentLineText);
                tagContentBuilder.Append(' ');
            }
        }

        string tagContent = tagContentBuilder.ToString();

        // 检查是否在属性值的引号内
        int quoteCount = tagContent.Count(c => c == '"');
        if (quoteCount % 2 == 1)
        {
            // 奇数个引号，说明在属性值内部
            context.ContextType = XmlContextType.AttributeValue;

            // 提取当前属性名：查找最近的 ' ' 或 '=' 前面的单词
            int eqPos = tagContent.LastIndexOf('=');
            if (eqPos > 0)
            {
                // 从等号向前找属性名
                int attrStart = eqPos - 1;
                while (attrStart >= 0 && (char.IsLetterOrDigit(tagContent[attrStart]) || tagContent[attrStart] == '.' || tagContent[attrStart] == '_' || tagContent[attrStart] == '-'))
                {
                    attrStart--;
                }
                attrStart++;
                context.CurrentAttribute = tagContent.Substring(attrStart, eqPos - attrStart).Trim();
            }

            // 提取标签名
            int tagNameEnd = tagContent.IndexOfAny(new char[] { ' ', '/', '>' });
            if (tagNameEnd > 1)
            {
                context.CurrentTag = tagContent.Substring(1, tagNameEnd - 1).Trim();
            }

            return context;
        }

        // 检查是否在属性名位置（前面有空格，后面可能有等号）
        if (tagContent.Contains(' '))
        {
            // 查找最后一个空格
            int lastSpace = tagContent.LastIndexOf(' ');
            if (lastSpace < tagContent.Length - 1)
            {
                // 检查后面是否有等号
                string afterSpace = tagContent.Substring(lastSpace + 1);
                if (!afterSpace.Contains('='))
                {
                    context.ContextType = XmlContextType.AttributeName;

                    // 提取标签名
                    int tagNameEnd = tagContent.IndexOf(' ');
                    if (tagNameEnd > 1)
                    {
                        context.CurrentTag = tagContent.Substring(1, tagNameEnd - 1).Trim();
                    }

                    return context;
                }
            }
        }

        // 检查是否在属性名位置（输入场景）
        if (tagContent.Contains(' '))
        {
            // 查找最后一个空格
            int lastSpace = tagContent.LastIndexOf(' ');
            if (lastSpace < tagContent.Length - 1)
            {
                // 检查后面是否有等号
                string afterSpace = tagContent.Substring(lastSpace + 1);
                if (!afterSpace.Contains('='))
                {
                    context.ContextType = XmlContextType.AttributeName;

                    // 提取标签名
                    int tagNameEnd = tagContent.IndexOf(' ');
                    if (tagNameEnd > 1)
                    {
                        context.CurrentTag = tagContent.Substring(1, tagNameEnd - 1).Trim();
                    }

                    return context;
                }
            }
        }

        // 检查是否在属性名位置（鼠标点击场景）
        if (tagContent.Contains(' '))
        {
            // 查找属性名边界
            int attrStart = tagContent.LastIndexOf(' ') + 1;
            int attrEnd = tagContent.Length;
            if (attrStart < attrEnd)
            {
                // 检查属性名是否合法
                bool isAttributeName = true;
                for (int i = attrStart; i < attrEnd; i++)
                {
                    char c = tagContent[i];
                    if (!char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-')
                    {
                        isAttributeName = false;
                        break;
                    }
                }

                if (isAttributeName)
                {
                    context.ContextType = XmlContextType.AttributeName;
                    context.CurrentAttribute = tagContent.Substring(attrStart, attrEnd - attrStart).Trim();

                    // 提取标签名
                    int tagNameEnd = tagContent.IndexOf(' ');
                    if (tagNameEnd > 1)
                    {
                        context.CurrentTag = tagContent.Substring(1, tagNameEnd - 1).Trim();
                    }

                    return context;
                }
            }
        }

        // 否则是标签名位置
        context.ContextType = XmlContextType.TagName;
        int nameEnd = tagContent.IndexOfAny(new char[] { ' ', '/', '>' });
        if (nameEnd == -1) nameEnd = tagContent.Length;

        if (tagContent.Length <= nameEnd && nameEnd > 1)
        {
            context.CurrentTag = tagContent.Substring(1, nameEnd - 1).Trim();
        }

        return context;
    }
}
