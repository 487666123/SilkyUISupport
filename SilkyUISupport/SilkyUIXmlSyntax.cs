using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace SilkyUISupport;

internal enum SilkyUIAttributeKind { None, Class, Name, Style }
internal enum SilkyUIXmlTagKind { Unknown, Body, Member, Style, Ordinary }

internal readonly struct SilkyUIXmlAttribute
{
    public SilkyUIXmlAttribute(string name, string value, int nameStart, int valueStart,
        int end, char quote, bool valueComplete)
    {
        Name = name;
        Value = value;
        NameStart = nameStart;
        ValueStart = valueStart;
        End = end;
        Quote = quote;
        ValueComplete = valueComplete;
    }

    public string Name { get; }
    public string Value { get; }
    public int NameStart { get; }
    public int NameEnd => NameStart + Name.Length;
    public int ValueStart { get; }
    public int End { get; }
    public char Quote { get; }
    public bool ValueComplete { get; }
}

internal sealed class SilkyUIXmlNamespaceScope
{
    private readonly SilkyUIXmlNamespaceScope _parent;
    private readonly Dictionary<string, string> _declarations;

    public SilkyUIXmlNamespaceScope(SilkyUIXmlNamespaceScope parent, Dictionary<string, string> declarations)
    {
        _parent = parent;
        _declarations = declarations;
    }

    public string Resolve(string prefix)
    {
        for (var scope = this; scope != null; scope = scope._parent)
            if (scope._declarations.TryGetValue(prefix, out var uri))
                return uri;
        return string.Empty;
    }

    public IEnumerable<string> GetPrefixes(string uri)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = this; scope != null; scope = scope._parent)
            foreach (var declaration in scope._declarations)
                if (seen.Add(declaration.Key) && declaration.Key.Length > 0 && declaration.Value == uri)
                    yield return declaration.Key;
    }
}

internal sealed class SilkyUIXmlTag
{
    public int Start { get; set; }
    public int NameStart { get; set; }
    public string Name { get; set; }
    public int ContentEnd { get; set; }
    public int End { get; set; }
    public bool IsComplete { get; set; }
    public bool IsClosing { get; set; }
    public bool IsSelfClosing { get; set; }
    public IReadOnlyList<SilkyUIXmlAttribute> Attributes { get; set; }
    public SilkyUIXmlNamespaceScope Scope { get; set; }
    public SilkyUIXmlNamespaceScope InheritedScope { get; set; }

    public SilkyUIXmlTagKind Kind => SilkyUIXmlSyntax.GetTagKind(Name, Scope);

    public bool TryGetSuiAttributeValue(SilkyUIAttributeKind kind, out string value)
    {
        foreach (var attribute in Attributes)
            if (attribute.ValueComplete && SilkyUIXmlSyntax.GetSuiAttributeKind(Scope, attribute.Name) == kind)
            {
                value = attribute.Value;
                return true;
            }
        value = string.Empty;
        return false;
    }
}

internal static class SilkyUIXmlSyntax
{
    public const string NamespaceUri = "https://github.com/487666123/SilkyUIFramework";
    public const string BindingNamespaceUri = "https://github.com/487666123/SilkyUIFramework/Binding";

    public static bool IsNamespaceDeclaration(string name)
        => name == "xmlns" || name?.StartsWith("xmlns:", StringComparison.Ordinal) == true;

    public static string GetLocalName(string name)
    {
        var separator = name?.IndexOf(':') ?? -1;
        return separator < 0 ? name ?? string.Empty : name.Substring(separator + 1);
    }

    public static string GetPrefix(string name)
    {
        var separator = name?.IndexOf(':') ?? -1;
        return separator < 0 ? string.Empty : name.Substring(0, separator);
    }

    public static bool IsMemberElementName(string name)
        => !string.IsNullOrEmpty(name) && name.IndexOf(':') < 0 && name.StartsWith("M.", StringComparison.Ordinal);

    public static bool IsOrdinaryElementName(string name)
        => !string.IsNullOrEmpty(name) && name.IndexOf(':') < 0 && name != "Body" && !IsMemberElementName(name);

    public static SilkyUIXmlTagKind GetTagKind(string name, SilkyUIXmlNamespaceScope scope)
    {
        if (name == "Body") return SilkyUIXmlTagKind.Body;
        if (IsMemberElementName(name)) return SilkyUIXmlTagKind.Member;
        if (GetLocalName(name) == "Style" && IsPrefixFor(scope, GetPrefix(name), NamespaceUri))
            return SilkyUIXmlTagKind.Style;
        return IsOrdinaryElementName(name) ? SilkyUIXmlTagKind.Ordinary : SilkyUIXmlTagKind.Unknown;
    }

    public static bool IsPrefixFor(SilkyUIXmlNamespaceScope scope, string prefix, string uri)
        => !string.IsNullOrEmpty(prefix) && scope?.Resolve(prefix) == uri;

    public static SilkyUIAttributeKind GetSuiAttributeKind(SilkyUIXmlNamespaceScope scope, string name)
    {
        if (!IsPrefixFor(scope, GetPrefix(name), NamespaceUri)) return SilkyUIAttributeKind.None;
        return GetLocalName(name) switch
        {
            "Class" => SilkyUIAttributeKind.Class,
            "Name" => SilkyUIAttributeKind.Name,
            "Style" => SilkyUIAttributeKind.Style,
            _ => SilkyUIAttributeKind.None
        };
    }

    public static bool TryGetBindingPropertyName(SilkyUIXmlNamespaceScope scope, string name, out string propertyName)
    {
        propertyName = IsPrefixFor(scope, GetPrefix(name), BindingNamespaceUri) ? GetLocalName(name) : string.Empty;
        return propertyName.Length > 0;
    }

    // Compatibility for callers that already have a bounded tag range.
    public static bool TryGetSuiAttributeKind(string text, int tagStart, int tagEnd, string name, out SilkyUIAttributeKind kind)
    {
        kind = GetSuiAttributeKind(GetTag(text, tagStart, tagEnd < 0 ? -1 : tagEnd + 1)?.Scope, name);
        return kind != SilkyUIAttributeKind.None;
    }

    public static SilkyUIXmlTag GetTag(string text, int tagStart, int scanEnd)
    {
        if (scanEnd < tagStart || tagStart < 0) return null;
        return EnumerateTags(text, scanEnd).FirstOrDefault(tag => tag.Start == tagStart);
    }

    public static int FindTagEnd(string text, int tagStart)
    {
        if (string.IsNullOrEmpty(text) || tagStart < 0 || tagStart >= text.Length) return -1;
        var end = FindTagBoundary(text, tagStart, text.Length);
        return end < text.Length && text[end] == '>' ? end : -1;
    }

    private static int FindTagBoundary(string text, int start, int limit)
    {
        var quote = '\0';
        for (var position = start + 1; position < limit; position++)
        {
            var current = text[position];
            if (quote != '\0')
            {
                if (current == quote) quote = '\0';
            }
            else if (current is '\'' or '"') quote = current;
            else if (current is '<' or '>') return position;
        }
        return limit;
    }

    // The same bounded, quote-aware token stream drives completion, classification and diagnostics.
    public static IEnumerable<SilkyUIXmlTag> EnumerateTags(string text, int limit = -1)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        limit = limit < 0 ? text.Length : Math.Min(limit, text.Length);
        var stack = new List<SilkyUIXmlTag>();
        var position = 0;
        while (position < limit)
        {
            var start = text.IndexOf('<', position, limit - position);
            if (start < 0) yield break;
            if (start + 1 < limit && text[start + 1] is '?' or '!')
            {
                position = SkipMarkup(text, start, limit);
                continue;
            }

            var boundary = FindTagBoundary(text, start, limit);
            var complete = boundary < limit && text[boundary] == '>';
            var nameStart = start + 1;
            var closing = nameStart < boundary && text[nameStart] == '/';
            if (closing) nameStart++;
            while (nameStart < boundary && char.IsWhiteSpace(text[nameStart])) nameStart++;
            var nameEnd = nameStart;
            while (nameEnd < boundary && IsNameChar(text[nameEnd])) nameEnd++;
            var name = text.Substring(nameStart, nameEnd - nameStart);
            var tail = boundary - 1;
            while (tail > nameEnd && char.IsWhiteSpace(text[tail])) tail--;
            var selfClosing = complete && tail >= nameEnd && text[tail] == '/';
            var attributes = closing ? new List<SilkyUIXmlAttribute>() : ReadAttributes(text, nameEnd, boundary);
            var parentScope = stack.Count > 0 ? stack[stack.Count - 1].Scope : null;
            var matchingIndex = -1;
            if (closing)
            {
                matchingIndex = stack.FindLastIndex(tag => tag.Name == name);
                if (matchingIndex >= 0) parentScope = stack[matchingIndex].Scope;
            }

            var tag = new SilkyUIXmlTag
            {
                Start = start, NameStart = nameStart, Name = name, ContentEnd = boundary,
                End = complete ? boundary + 1 : boundary, IsComplete = complete,
                IsClosing = closing, IsSelfClosing = selfClosing, Attributes = attributes,
                InheritedScope = parentScope, Scope = CreateScope(parentScope, attributes)
            };
            yield return tag;

            if (closing && matchingIndex >= 0)
                stack.RemoveRange(matchingIndex, stack.Count - matchingIndex);
            else if (!closing && !selfClosing && name.Length > 0)
                stack.Add(tag);
            position = tag.End;
        }
    }

    private static SilkyUIXmlNamespaceScope CreateScope(
        SilkyUIXmlNamespaceScope parent, IEnumerable<SilkyUIXmlAttribute> attributes)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
            if (IsNamespaceDeclaration(attribute.Name))
            {
                var prefix = attribute.Name == "xmlns" ? string.Empty : attribute.Name.Substring(6);
                declarations[prefix] = DecodeNamespaceValue(attribute);
            }
        return new SilkyUIXmlNamespaceScope(parent, declarations);
    }

    /// <summary>Project cached tokens up to the caret without re-scanning text or importing future declarations.</summary>
    public static SilkyUIXmlTag GetIncompletePrefix(SilkyUIXmlTag tag, int position)
    {
        position = Math.Max(tag.Start + 1, Math.Min(position, tag.ContentEnd));
        if (position == tag.ContentEnd) return tag;
        var attributes = new List<SilkyUIXmlAttribute>();
        foreach (var attribute in tag.Attributes)
        {
            if (attribute.NameStart >= position) break;
            if (attribute.End <= position) { attributes.Add(attribute); continue; }
            var name = attribute.Name.Substring(0, Math.Min(attribute.Name.Length, position - attribute.NameStart));
            var hasValue = attribute.ValueStart >= 0 && attribute.ValueStart <= position;
            var value = hasValue
                ? attribute.Value.Substring(0, Math.Min(attribute.Value.Length, position - attribute.ValueStart))
                : string.Empty;
            attributes.Add(new SilkyUIXmlAttribute(name, value, attribute.NameStart,
                hasValue ? attribute.ValueStart : -1, position, hasValue ? attribute.Quote : '\0', false));
        }
        return new SilkyUIXmlTag
        {
            Start = tag.Start, NameStart = Math.Min(tag.NameStart, position),
            Name = tag.Name.Substring(0, Math.Max(0, Math.Min(tag.Name.Length, position - tag.NameStart))),
            ContentEnd = position, End = position, IsClosing = tag.IsClosing,
            Attributes = attributes, InheritedScope = tag.InheritedScope,
            Scope = CreateScope(tag.InheritedScope, attributes)
        };
    }

    private static List<SilkyUIXmlAttribute> ReadAttributes(string text, int position, int limit)
    {
        var attributes = new List<SilkyUIXmlAttribute>();
        while (position < limit)
        {
            while (position < limit && char.IsWhiteSpace(text[position])) position++;
            if (position >= limit || text[position] == '/') break;
            var nameStart = position;
            while (position < limit && IsNameChar(text[position])) position++;
            if (position == nameStart) { position++; continue; }
            var name = text.Substring(nameStart, position - nameStart);
            var nameEnd = position;
            while (position < limit && char.IsWhiteSpace(text[position])) position++;
            var valueStart = -1;
            var value = string.Empty;
            var quote = '\0';
            var valueComplete = false;
            var attributeEnd = nameEnd;
            if (position < limit && text[position] == '=')
            {
                position++;
                while (position < limit && char.IsWhiteSpace(text[position])) position++;
                if (position < limit && text[position] is '\'' or '"') quote = text[position++];
                valueStart = position;
                if (quote != '\0')
                {
                    while (position < limit && text[position] != quote) position++;
                    value = text.Substring(valueStart, position - valueStart);
                    valueComplete = position < limit;
                    if (valueComplete) position++;
                }
                else
                {
                    while (position < limit && !char.IsWhiteSpace(text[position]) && text[position] != '/') position++;
                    value = text.Substring(valueStart, position - valueStart);
                }
                attributeEnd = position;
            }
            attributes.Add(new SilkyUIXmlAttribute(name, value, nameStart, valueStart, attributeEnd, quote, valueComplete));
        }
        return attributes;
    }

    private static string DecodeNamespaceValue(SilkyUIXmlAttribute attribute)
    {
        if (!attribute.ValueComplete) return string.Empty;
        if (attribute.Value.IndexOf('&') < 0) return attribute.Value;
        try
        {
            // Use the XML library for entity decoding; never resolve a document DTD or external resource.
            using var reader = XmlReader.Create(new StringReader("<n a=" + attribute.Quote + attribute.Value + attribute.Quote + "/>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.Read();
            return reader.GetAttribute("a") ?? string.Empty;
        }
        catch (XmlException) { return string.Empty; }
    }

    private static int SkipMarkup(string text, int start, int limit)
    {
        string terminator = null;
        if (StartsWith(text, start, limit, "<!--")) terminator = "-->";
        else if (StartsWith(text, start, limit, "<![CDATA[")) terminator = "]]>";
        else if (text[start + 1] == '?') terminator = "?>";
        if (terminator != null)
        {
            var end = text.IndexOf(terminator, start + 2, limit - start - 2, StringComparison.Ordinal);
            return end < 0 ? limit : end + terminator.Length;
        }

        var quote = '\0';
        var brackets = 0;
        for (var position = start + 2; position < limit; position++)
        {
            var current = text[position];
            if (quote != '\0') { if (current == quote) quote = '\0'; }
            else if (current is '\'' or '"') quote = current;
            else if (current == '[') brackets++;
            else if (current == ']') brackets--;
            else if (current == '>' && brackets == 0) return position + 1;
        }
        return limit;
    }

    private static bool StartsWith(string text, int start, int limit, string value)
        => start + value.Length <= limit && string.CompareOrdinal(text, start, value, 0, value.Length) == 0;

    public static IEnumerable<string> GetStyleNames(string text)
    {
        foreach (var tag in EnumerateTags(text))
            if (!tag.IsClosing && tag.Kind == SilkyUIXmlTagKind.Style &&
                tag.TryGetSuiAttributeValue(SilkyUIAttributeKind.Name, out var name) && !string.IsNullOrWhiteSpace(name))
                yield return name;
    }

    internal static bool IsNameChar(char value)
        => char.IsLetterOrDigit(value) || value is ':' or '.' or '_' or '-';
}
