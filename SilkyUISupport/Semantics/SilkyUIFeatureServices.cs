using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SilkyUISupport;

internal enum SilkyUIClassificationKind { Element, UnknownElement, Attribute, SpecialAttribute, UnknownAttribute }
internal sealed record SilkyUIClassification(int Start, int Length, SilkyUIClassificationKind Kind);

internal static class SilkyUICompletionEngine
{
    public static ImmutableArray<SilkyUICompletionItem> GetItems(
        SilkyUISemanticModel model, XmlContext context, IEnumerable<string> styleNames)
    {
        var items = new List<SilkyUICompletionItem>();
        var tag = context.Tag;
        var suiPrefixes = tag?.Scope.GetPrefixes(SilkyUIXmlSyntax.NamespaceUri) ?? [];
        var bindingPrefixes = tag?.Scope.GetPrefixes(SilkyUIXmlSyntax.BindingNamespaceUri) ?? [];

        switch (context.ContextType)
        {
            case XmlContextType.TagName:
                if (context.CurrentTag == "M" || context.CurrentTag.StartsWith("M.", StringComparison.Ordinal))
                    AddMembers(items, model.GetExpandableParentProperties(tag));
                else
                {
                    Add(items, "Body", "Body", "根元素", SilkyUICompletionItemKind.Class);
                    foreach (var prefix in suiPrefixes) AddDirective(items, prefix, "Style", "SilkyUI 样式元素");
                    foreach (var mapped in model.Metadata.Classes)
                        Add(items, mapped.Alias, mapped.Alias, mapped.Class.ToDisplayString(), SilkyUICompletionItemKind.Class,
                            mapped.Class.ToDisplayString());
                }
                break;
            case XmlContextType.AttributeName:
                AddAttributeNameItems(items, model, context, suiPrefixes, bindingPrefixes);
                break;
            case XmlContextType.AttributeValue:
                AddAttributeValueItems(items, model, context, styleNames);
                break;
        }
        return [.. items];
    }

    private static void AddAttributeNameItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model,
        XmlContext context, IEnumerable<string> suiPrefixes, IEnumerable<string> bindingPrefixes)
    {
        var tag = context.Tag;
        if (tag == null) return;
        var element = model.ResolveElement(tag);
        if (tag.Kind == SilkyUIXmlTagKind.Style)
        {
            AddProperties(items, element.Properties, false);
            foreach (var directivePrefix in suiPrefixes)
            {
                AddDirective(items, directivePrefix, "Name", "定义样式名称");
                AddDirective(items, directivePrefix, "Target", "指定样式属性来源类全名");
            }
            return;
        }

        var prefix = SilkyUIXmlSyntax.GetPrefix(context.CurrentAttribute);
        var hasColon = context.CurrentAttribute.IndexOf(':') >= 0;
        var canBind = model.CanBind(tag);
        if (canBind && hasColon && bindingPrefixes.Contains(prefix, StringComparer.Ordinal))
        {
            AddBindings(items, prefix, element.Properties);
            return;
        }

        if (!hasColon)
        {
            AddProperties(items, element.Properties, true);
            if (canBind)
                foreach (var bindingPrefix in bindingPrefixes) AddBindings(items, bindingPrefix, element.Properties);
        }

        foreach (var suiPrefix in suiPrefixes)
        {
            if (hasColon && prefix != suiPrefix) continue;
            if (tag.Kind == SilkyUIXmlTagKind.Body) AddDirective(items, suiPrefix, "Class", "指定 UIElementGroup 子类全名");
            else if (tag.Kind == SilkyUIXmlTagKind.Ordinary) AddDirective(items, suiPrefix, "Name", "生成 C# 控件属性");
            if (tag.Kind is SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary or SilkyUIXmlTagKind.Member)
                AddDirective(items, suiPrefix, "Style", "引用一个或多个样式");
        }
    }

    private static void AddAttributeValueItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model,
        XmlContext context, IEnumerable<string> styleNames)
    {
        if (SilkyUIXmlSyntax.IsNamespaceDeclaration(context.CurrentAttribute))
        {
            Add(items, SilkyUIXmlSyntax.NamespaceUri, SilkyUIXmlSyntax.NamespaceUri, "SilkyUIFramework 命名空间 URI", SilkyUICompletionItemKind.Property);
            Add(items, SilkyUIXmlSyntax.BindingNamespaceUri, SilkyUIXmlSyntax.BindingNamespaceUri, "SilkyUIFramework 绑定命名空间 URI", SilkyUICompletionItemKind.Property);
            return;
        }

        var tag = context.Tag;
        if (tag == null) return;
        var synthetic = new SilkyUIXmlAttribute(context.CurrentAttribute, string.Empty, 0, -1, 0, '\0', false);
        var attribute = model.ResolveAttribute(tag, synthetic);
        if (tag.Kind == SilkyUIXmlTagKind.Style && attribute.DirectiveKind == SilkyUIAttributeKind.Target)
        {
            foreach (var target in model.Metadata.TargetClasses)
                Add(items, target.Class.Name, target.FullName, target.FullName, SilkyUICompletionItemKind.Class, target.FullName);
            return;
        }
        if (tag.Kind == SilkyUIXmlTagKind.Body && attribute.DirectiveKind == SilkyUIAttributeKind.Class)
        {
            foreach (var group in model.Metadata.GroupClasses)
                Add(items, group.Name, group.FullName, group.FullName, SilkyUICompletionItemKind.Class, group.FullName);
            return;
        }
        if (attribute.DirectiveKind == SilkyUIAttributeKind.Style)
        {
            foreach (var name in styleNames) Add(items, name, name, "样式名称", SilkyUICompletionItemKind.Property);
            return;
        }
        if (context.CurrentAttribute.IndexOf(':') >= 0) return;
        var property = model.ResolveAttribute(tag, synthetic).Property;
        if (property?.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public) return;
        foreach (var value in property.Enums) Add(items, value, value, value, SilkyUICompletionItemKind.Enumeration);
    }

    private static void AddMembers(List<SilkyUICompletionItem> items, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
            Add(items, $"M.{property.Property.Name}", $"M.{property.Property.Name}",
                property.Property.Type.ToDisplayString(), SilkyUICompletionItemKind.Property, property.Property.Type.ToDisplayString());
    }

    private static void AddProperties(List<SilkyUICompletionItem> items, IEnumerable<SilkyUIProperty> properties, bool requireWritable)
    {
        foreach (var property in properties)
        {
            if (requireWritable && property.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public) continue;
            Add(items, property.Property.Name, property.Property.Name, property.Property.Type.ToDisplayString(),
                SilkyUICompletionItemKind.Property, property.Property.Type.ToDisplayString());
        }
    }

    private static void AddBindings(List<SilkyUICompletionItem> items, string prefix, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            if (property.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public) continue;
            Add(items, $"{prefix}:{property.Property.Name}", $"{prefix}:{property.Property.Name}",
                property.Property.Type.ToDisplayString(), SilkyUICompletionItemKind.Property, property.Property.Type.ToDisplayString());
        }
    }

    private static void AddDirective(List<SilkyUICompletionItem> items, string prefix, string localName, string description)
    {
        if (!string.IsNullOrEmpty(prefix)) Add(items, $"{prefix}:{localName}", $"{prefix}:{localName}", description, SilkyUICompletionItemKind.Property);
    }

    private static void Add(List<SilkyUICompletionItem> items, string display, string insertion, string description,
        SilkyUICompletionItemKind kind, string suffix = "") => items.Add(new(display, insertion, description, kind, suffix));
}

internal static class SilkyUIDiagnosticAnalyzer
{
    public static ImmutableArray<SilkyUIDiagnostic> Analyze(SilkyUISemanticModel model)
    {
        var diagnostics = new List<SilkyUIDiagnostic>();
        foreach (var tag in model.Document.Tags)
        {
            if (tag.IsClosing || tag.Name.Length == 0) continue;
            var element = model.ResolveElement(tag);
            if (!element.IsKnown)
            {
                diagnostics.Add(new(tag.NameStart, tag.Name.Length, $"未知元素 '{tag.Name}'"));
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in tag.Attributes)
            {
                var spanStart = attribute.NameStart;
                var spanLength = attribute.Name.Length;
                if (SilkyUIXmlSyntax.IsNamespaceDeclaration(attribute.Name)) continue;
                if (!seen.Add(attribute.Name))
                {
                    diagnostics.Add(new(spanStart, spanLength, $"重复属性 '{attribute.Name}'"));
                    continue;
                }

                var resolved = model.ResolveAttribute(tag, attribute);
                if (resolved.Kind == SilkyUISemanticAttributeKind.Directive)
                {
                    if (tag.Kind == SilkyUIXmlTagKind.Body && resolved.DirectiveKind == SilkyUIAttributeKind.Class &&
                        attribute.ValueComplete && !string.IsNullOrWhiteSpace(attribute.Value) && element.BodyClass == null)
                        diagnostics.Add(ValueDiagnostic(attribute, $"未知类 '{attribute.Value}'"));
                    continue;
                }
                if (resolved.Kind == SilkyUISemanticAttributeKind.Binding)
                {
                    if (resolved.HasKnownProperties && resolved.Property == null)
                        diagnostics.Add(new(spanStart, spanLength, $"'{tag.Name}' 上没有可绑定的 '{SilkyUIXmlSyntax.GetLocalName(attribute.Name)}' 属性"));
                    else if (resolved.Property?.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                        diagnostics.Add(new(spanStart, spanLength, $"'{SilkyUIXmlSyntax.GetLocalName(attribute.Name)}' 不支持绑定"));
                    continue;
                }
                if (resolved.Kind == SilkyUISemanticAttributeKind.UnsupportedNamespace)
                {
                    diagnostics.Add(new(spanStart, spanLength, $"不支持命名空间属性 '{attribute.Name}'"));
                    continue;
                }
                if (resolved.Kind == SilkyUISemanticAttributeKind.Unknown && resolved.HasKnownProperties)
                {
                    diagnostics.Add(new(spanStart, spanLength, $"'{tag.Name}' 上没有 '{attribute.Name}' 属性"));
                    continue;
                }
                if (resolved.Kind == SilkyUISemanticAttributeKind.Property &&
                    resolved.Property?.Property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    diagnostics.Add(new(spanStart, spanLength, $"'{attribute.Name}' 不支持赋值"));
                    continue;
                }
                if (resolved.Property != null && attribute.ValueComplete && attribute.Value.Length > 0 && resolved.Property.Enums.Length > 0 &&
                    !resolved.Property.Enums.Contains(attribute.Value, StringComparer.Ordinal))
                    diagnostics.Add(ValueDiagnostic(attribute, $"'{attribute.Name}' 可选值: {string.Join(", ", resolved.Property.Enums)}"));
            }
        }
        return [.. diagnostics];
    }

    private static SilkyUIDiagnostic ValueDiagnostic(SilkyUIXmlAttribute attribute, string message)
        => new(attribute.ValueStart >= 0 ? attribute.ValueStart : attribute.NameStart,
            attribute.ValueStart >= 0 ? attribute.Value.Length : attribute.Name.Length, message);
}

internal static class SilkyUIClassificationService
{
    public static ImmutableArray<SilkyUIClassification> GetClassifications(SilkyUISemanticModel model)
    {
        var results = new List<SilkyUIClassification>();
        foreach (var tag in model.Document.Tags)
        {
            if (tag.Name.Length == 0) continue;
            var element = model.ResolveElement(tag);
            results.Add(new(tag.NameStart, tag.Name.Length, element.IsKnown
                ? SilkyUIClassificationKind.Element : SilkyUIClassificationKind.UnknownElement));
            if (tag.IsClosing) continue;
            foreach (var attribute in tag.Attributes)
            {
                var resolved = model.ResolveAttribute(tag, attribute);
                var kind = resolved.Kind switch
                {
                    SilkyUISemanticAttributeKind.Namespace => SilkyUIClassificationKind.Attribute,
                    SilkyUISemanticAttributeKind.Directive or SilkyUISemanticAttributeKind.Binding => SilkyUIClassificationKind.SpecialAttribute,
                    SilkyUISemanticAttributeKind.Property => SilkyUIClassificationKind.Attribute,
                    _ => SilkyUIClassificationKind.UnknownAttribute
                };
                results.Add(new(attribute.NameStart, attribute.Name.Length, kind));
            }
        }
        return [.. results];
    }
}
