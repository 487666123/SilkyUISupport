using System;
using System.Collections.Generic;
using Microsoft.Language.Xml;

namespace SilkyUISupport;

/// <summary>将库的语法树适配为编辑器模型，保留原始源码范围。</summary>
internal static class SilkyUIXmlParser
{
    private const string FragmentPrefix = "<__SilkyUISupport_DocumentFragment__>";

    public static SilkyUIXmlTag[] Parse(string text)
    {
        var tags = new List<SilkyUIXmlTag>();
        var syntax = Parser.ParseText(text);
        var sourceOffset = 0;
        // 某些格式错误的文档声明会被库丢弃。
        // 片段解析将其保留为 trivia；永远不暴露偏移后的源码范围。
        if (syntax.FullWidth != text.Length)
        {
            syntax = Parser.ParseText(FragmentPrefix + text);
            sourceOffset = -FragmentPrefix.Length;
            if (syntax.FullWidth != text.Length - sourceOffset) return [];
        }
        AddTags(text, syntax, sourceOffset, tags, sourceOffset != 0);

        // 文档语法将多余的根元素和末尾的 '<' 放入跳过 token 中。
        // 将该后缀作为元素内容解析，使根元素后的编辑仍能工作。
        // 明确排除合成的包装器开标签；所有其他范围映射到源码后缀。
        var recoveryStart = syntax.SkippedTokens == null ? text.Length : sourceOffset + syntax.SkippedTokens.Start;
        while (recoveryStart < text.Length)
        {
            var offset = recoveryStart - FragmentPrefix.Length;
            var fragment = Parser.ParseText(FragmentPrefix + text.Substring(recoveryStart));
            if (fragment.FullWidth != text.Length - offset) break;
            AddTags(text, fragment, offset, tags, true);
            var nextStart = fragment.SkippedTokens == null
                ? text.Length
                : offset + fragment.SkippedTokens.Start;
            if (nextStart <= recoveryStart) break;
            recoveryStart = nextStart;
        }

        tags.Sort((left, right) => left.Start.CompareTo(right.Start));
        return tags.ToArray();
    }

    private static void AddTags(string text, XmlDocumentSyntax syntax, int offset, List<SilkyUIXmlTag> tags, bool hasWrapper)
    {
        var openings = new Dictionary<IXmlElementSyntax, SilkyUIXmlTag>();
        foreach (var node in syntax.DescendantNodes(null, false))
        {
            if (node is not IXmlElementSyntax element) continue;
            SilkyUIXmlTag parent = null;
            if (element.Parent != null) openings.TryGetValue(element.Parent, out parent);
            var inheritedScope = parent?.Scope;

            if (node is XmlElementSyntax paired)
            {
                var isWrapper = hasWrapper && ReferenceEquals(node, syntax.RootSyntax.AsNode);
                var opening = isWrapper ? null : CreateTag(text, paired.StartTag, paired.StartTag.LessThanToken,
                    paired.StartTag.NameNode, paired.StartTag.GreaterThanToken, element.Attributes,
                    offset, false, false, parent, inheritedScope);
                openings[element] = opening ?? parent;
                if (opening != null) tags.Add(opening);

                var closing = CreateTag(text, paired.EndTag, paired.EndTag.LessThanSlashToken,
                    paired.EndTag.NameNode, paired.EndTag.GreaterThanToken, null,
                    offset, true, false, null, opening?.Scope ?? inheritedScope);
                if (closing == null) continue;
                // 恢复机制可能将不匹配的结束标签附加到缺失开始标签的元素上。
                // 只有真正匹配的标签对才能提供成员导航的父级关系。
                if (opening != null && closing.Name == opening.Name)
                {
                    closing.Parent = parent;
                    closing.MatchingOpeningTag = opening;
                }
                tags.Add(closing);
            }
            else if (node is XmlEmptyElementSyntax empty)
            {
                var tag = CreateTag(text, empty, empty.LessThanToken, empty.NameNode,
                    empty.SlashGreaterThanToken, element.Attributes,
                    offset, false, true, parent, inheritedScope);
                openings[element] = tag ?? parent;
                if (tag != null) tags.Add(tag);
            }
        }
    }

    private static SilkyUIXmlTag CreateTag(
        string text, SyntaxNode header, SyntaxNode openingToken, XmlNameSyntax nameNode,
        SyntaxNode terminal, IEnumerable<XmlAttributeSyntax> attributeNodes, int offset,
        bool isClosing, bool isEmptyElement, SilkyUIXmlTag parent, SilkyUIXmlNamespaceScope inheritedScope)
    {
        var start = offset + openingToken.SpanStart;
        // 缺失的 token 可能携带跳过的 trivia，因此 FullWidth 不是存在性检查。
        if (openingToken.Width == 0 || start < 0 || start >= text.Length) return null;

        var terminalEnd = offset + terminal.SpanStart + terminal.Width;
        var complete = terminal.Width > 0 && terminalEnd <= text.Length && text[terminalEnd - 1] == '>';
        var end = Math.Min(text.Length, complete ? terminalEnd : offset + header.End);
        var contentEnd = complete ? end - 1 : end;
        var nameStart = Clamp(offset + nameNode.SpanStart, start + openingToken.Width, contentEnd);
        var nameEnd = Clamp(offset + nameNode.SpanStart + nameNode.Width, nameStart, contentEnd);
        var attributes = ReadAttributes(text, attributeNodes, offset, contentEnd);
        return new SilkyUIXmlTag
        {
            Name = text.Substring(nameStart, nameEnd - nameStart),
            Start = start,
            NameStart = nameStart,
            ContentEnd = contentEnd,
            End = end,
            IsClosing = isClosing,
            IsComplete = complete,
            IsSelfClosing = isEmptyElement && complete,
            Attributes = attributes,
            Parent = parent,
            InheritedScope = inheritedScope,
            Scope = SilkyUIXmlSyntax.CreateScope(inheritedScope, attributes)
        };
    }

    private static List<SilkyUIXmlAttribute> ReadAttributes(
        string text, IEnumerable<XmlAttributeSyntax> nodes, int offset, int contentEnd)
    {
        var attributes = new List<SilkyUIXmlAttribute>();
        if (nodes == null) return attributes;
        foreach (var node in nodes)
        {
            var nameStart = Clamp(offset + node.NameNode.SpanStart, 0, contentEnd);
            var nameEnd = Clamp(offset + node.NameNode.SpanStart + node.NameNode.Width, nameStart, contentEnd);
            if (nameStart == nameEnd) continue;
            var name = text.Substring(nameStart, nameEnd - nameStart);
            if (node.Equals.Width == 0 || node.ValueNode is not XmlStringSyntax value)
            {
                attributes.Add(new SilkyUIXmlAttribute(name, string.Empty, nameStart, -1, nameEnd, '\0', false));
                continue;
            }

            var firstQuote = value.StartQuoteToken;
            var lastQuote = value.EndQuoteToken;
            var quotePosition = offset + firstQuote.SpanStart;
            var hasQuote = firstQuote.Width > 0 && quotePosition >= 0 && quotePosition < contentEnd &&
                text[quotePosition] is '\'' or '"';
            var quote = hasQuote ? text[quotePosition] : '\0';
            var valueStart = Clamp(offset + (hasQuote ? firstQuote.SpanStart + firstQuote.Width : value.SpanStart),
                nameEnd, contentEnd);
            var valueEnd = Clamp(offset + (hasQuote ? lastQuote.SpanStart : value.SpanStart + value.Width),
                valueStart, contentEnd);
            var complete = hasQuote && firstQuote.Width == 1 && lastQuote.Width == 1 &&
                valueEnd < contentEnd && text[valueEnd] == quote;
            var end = complete ? valueEnd + lastQuote.Width : valueEnd;
            // Retain the raw source spelling and its length for completion/diagnostic spans.
            // Namespace declarations are decoded separately by the existing scope policy.
            attributes.Add(new SilkyUIXmlAttribute(name, text.Substring(valueStart, valueEnd - valueStart),
                nameStart, valueStart, end, quote, complete));
        }
        return attributes;
    }

    private static int Clamp(int position, int minimum, int maximum)
        => Math.Max(minimum, Math.Min(position, maximum));
}
