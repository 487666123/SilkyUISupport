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
                AddElementItems(items, model, context, suiPrefixes);
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

    private static void AddElementItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model,
        XmlContext context, IEnumerable<string> suiPrefixes)
    {
        var hasPrefix = context.CurrentTag.IndexOf(':') >= 0;
        var typedPrefix = SilkyUIXmlSyntax.GetPrefix(context.CurrentTag);
        var scope = context.Tag?.Scope;
        if (!hasPrefix && string.IsNullOrEmpty(scope?.Resolve(string.Empty)))
        {
            Add(items, "Body", "Body", "根元素", SilkyUICompletionItemKind.Class);
            foreach (var mapped in model.Metadata.Classes)
                Add(items, mapped.Alias, mapped.Alias, mapped.Class.ToDisplayString(), SilkyUICompletionItemKind.Class,
                    mapped.Class.ToDisplayString());
        }
        foreach (var prefix in suiPrefixes)
            if (!hasPrefix || prefix == typedPrefix) AddDirective(items, prefix, "Style", "SilkyUI 样式元素");

        if (scope == null) return;
        foreach (var declaration in scope.GetDeclarations())
            if (declaration.Value == SilkyUIXmlSyntax.PropertiesNamespaceUri &&
                (!hasPrefix || declaration.Key == typedPrefix))
                AddMembers(items, declaration.Key, model.GetExpandableParentProperties(context.Tag));

        if (model.ClrProject == null) return;
        foreach (var declaration in scope.GetDeclarations())
        {
            if (!SilkyUIClrNamespace.IsClrNamespace(declaration.Value) ||
                (hasPrefix && declaration.Key != typedPrefix)) continue;
            foreach (var type in model.ClrProject.GetTypes(declaration.Value, model.ClrAccessContext, out _))
            {
                var name = declaration.Key.Length == 0 ? type.Name : declaration.Key + ":" + type.Name;
                Add(items, name, name, type.ToDisplayString(), SilkyUICompletionItemKind.Class,
                    type.ContainingAssembly.Name);
            }
        }
    }

    private static void AddAttributeNameItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model,
        XmlContext context, IEnumerable<string> suiPrefixes, IEnumerable<string> bindingPrefixes)
    {
        var tag = context.Tag;
        if (tag == null) return;
        var prefix = SilkyUIXmlSyntax.GetPrefix(context.CurrentAttribute);
        var hasColon = context.CurrentAttribute.IndexOf(':') >= 0;
        if (tag.Parent == null && !tag.IsClosing)
            foreach (var suiPrefix in suiPrefixes)
                if (!hasColon || prefix == suiPrefix)
                    AddDirective(items, suiPrefix, "Class", "指定 UIElementGroup 子类全名");

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
            if (tag.Kind == SilkyUIXmlTagKind.Ordinary) AddDirective(items, suiPrefix, "Name", "生成 C# 控件属性");
            if (tag.Kind is SilkyUIXmlTagKind.Body or SilkyUIXmlTagKind.Ordinary or SilkyUIXmlTagKind.Member)
                AddDirective(items, suiPrefix, "Style", "引用一个或多个样式");
        }
    }

    private static void AddAttributeValueItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model,
        XmlContext context, IEnumerable<string> styleNames)
    {
        if (SilkyUIXmlSyntax.IsNamespaceDeclaration(context.CurrentAttribute))
        {
            AddNamespaceItems(items, model, context.CurrentValue);
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
        if (tag.Parent == null && !tag.IsClosing && attribute.DirectiveKind == SilkyUIAttributeKind.Class)
        {
            foreach (var group in model.Metadata.GroupClasses)
                if (model.ClrProject?.Compilation.Assembly.GetTypeByMetadataName(group.FullName) != null ||
                    string.IsNullOrEmpty(model.Document.FilePath))
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

    private static void AddNamespaceItems(List<SilkyUICompletionItem> items, SilkyUISemanticModel model, string input)
    {
        const string clrPrefix = "clr-namespace:";
        if (!input.StartsWith(clrPrefix, StringComparison.Ordinal))
        {
            Add(items, SilkyUIXmlSyntax.NamespaceUri, SilkyUIXmlSyntax.NamespaceUri, "SilkyUIFramework 命名空间 URI", SilkyUICompletionItemKind.Property);
            Add(items, SilkyUIXmlSyntax.BindingNamespaceUri, SilkyUIXmlSyntax.BindingNamespaceUri, "SilkyUIFramework 绑定命名空间 URI", SilkyUICompletionItemKind.Property);
            Add(items, SilkyUIXmlSyntax.PropertiesNamespaceUri, SilkyUIXmlSyntax.PropertiesNamespaceUri, "SilkyUIFramework 属性节点命名空间 URI", SilkyUICompletionItemKind.Property);
            Add(items, clrPrefix, clrPrefix, "从 C# 命名空间导入类型", SilkyUICompletionItemKind.Property);
            return;
        }

        if (input.IndexOf(';') >= 0 || model.ClrProject == null) return;
        foreach (var ns in model.ClrProject.GetNamespaceNames())
            Add(items, clrPrefix + ns, clrPrefix + ns, "当前项目可见的 CLR 命名空间", SilkyUICompletionItemKind.Property);
    }

    private static void AddMembers(List<SilkyUICompletionItem> items, string prefix, IEnumerable<SilkyUIProperty> properties)
    {
        foreach (var property in properties)
        {
            var name = prefix.Length == 0 ? property.Property.Name : prefix + ":" + property.Property.Name;
            Add(items, name, name, property.Property.Type.ToDisplayString(),
                SilkyUICompletionItemKind.Property, property.Property.Type.ToDisplayString());
        }
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
            foreach (var declaration in tag.Attributes)
            {
                if (!declaration.ValueComplete || !SilkyUIXmlSyntax.IsNamespaceDeclaration(declaration.Name)) continue;
                var prefix = declaration.Name == "xmlns" ? string.Empty : declaration.Name.Substring(6);
                var uri = tag.Scope.Resolve(prefix);
                if (!uri.StartsWith("clr-namespace", StringComparison.Ordinal)) continue;
                var parsed = SilkyUIClrNamespace.TryParse(uri, out _, out var error);
                if (parsed)
                    error = model.ClrProject == null
                        ? "无法确定此 XML 所属的 C# 项目，或项目元数据尚未就绪"
                        : model.ClrProject.ValidateNamespace(uri);
                if (error != null) diagnostics.Add(ValueDiagnostic(declaration, error));
            }
            var element = model.ResolveElement(tag);
            if (!element.IsKnown)
            {
                diagnostics.Add(new(tag.NameStart, tag.Name.Length, element.Error ?? $"未知元素 '{tag.Name}'"));
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
