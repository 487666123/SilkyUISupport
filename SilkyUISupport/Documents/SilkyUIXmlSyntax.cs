using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace SilkyUISupport;

internal enum SilkyUIAttributeKind { None, Class, Name, Style, Target }
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

    public IEnumerable<KeyValuePair<string, string>> GetDeclarations()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = this; scope != null; scope = scope._parent)
            foreach (var declaration in scope._declarations)
                if (seen.Add(declaration.Key)) yield return declaration;
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
    public SilkyUIXmlTag Parent { get; set; }
    public SilkyUIXmlTag MatchingOpeningTag { get; set; }

    public SilkyUIXmlTagKind Kind =>
        (IsClosing && MatchingOpeningTag?.Kind == SilkyUIXmlTagKind.Body) ||
        (!IsClosing && Parent == null && TryGetSuiAttributeValue(SilkyUIAttributeKind.Class, out _))
            ? SilkyUIXmlTagKind.Body : SilkyUIXmlSyntax.GetTagKind(Name, Scope);

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
    public const string PropertiesNamespaceUri = "https://github.com/487666123/SilkyUIFramework/Properties";

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

    public static SilkyUIXmlTagKind GetTagKind(string name, SilkyUIXmlNamespaceScope scope)
    {
        if (string.IsNullOrEmpty(name)) return SilkyUIXmlTagKind.Unknown;
        var uri = scope?.Resolve(GetPrefix(name)) ?? string.Empty;
        if (uri == PropertiesNamespaceUri) return SilkyUIXmlTagKind.Member;
        if (name == "Body" && uri.Length == 0) return SilkyUIXmlTagKind.Body;
        if (GetLocalName(name) == "Style" && IsPrefixFor(scope, GetPrefix(name), NamespaceUri))
            return SilkyUIXmlTagKind.Style;
        return SilkyUIXmlTagKind.Ordinary;
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
            "Target" => SilkyUIAttributeKind.Target,
            _ => SilkyUIAttributeKind.None
        };
    }

    public static bool TryGetBindingPropertyName(SilkyUIXmlNamespaceScope scope, string name, out string propertyName)
    {
        propertyName = IsPrefixFor(scope, GetPrefix(name), BindingNamespaceUri) ? GetLocalName(name) : string.Empty;
        return propertyName.Length > 0;
    }

    internal static SilkyUIXmlNamespaceScope CreateScope(
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

    /// <summary>投影缓存的 token 直到光标位置，不重新扫描文本或导入后续声明。</summary>
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
            Attributes = attributes, Parent = tag.Parent, InheritedScope = tag.InheritedScope,
            MatchingOpeningTag = tag.MatchingOpeningTag,
            Scope = CreateScope(tag.InheritedScope, attributes)
        };
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

    internal static bool IsNameChar(char value)
        => char.IsLetterOrDigit(value) || value is ':' or '.' or '_' or '-';
}
