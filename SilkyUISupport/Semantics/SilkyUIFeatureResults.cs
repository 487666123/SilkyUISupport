using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SilkyUISupport;

internal enum SilkyUICompletionItemKind { Class, Property, Enumeration }
internal enum SilkyUISemanticAttributeKind { Unknown, Namespace, Directive, Binding, Property, UnsupportedNamespace }
internal enum SilkyUISymbolKind { Element, Member, Attribute }

/// <summary>独立于 SDK 的补全数据；Visual Studio 适配器负责展示细节。</summary>
internal sealed record SilkyUICompletionItem(
    string DisplayText, string InsertionText, string Description, SilkyUICompletionItemKind Kind, string Suffix = "");

/// <summary>独立于 SDK 的诊断数据，使用快照坐标表达。</summary>
internal sealed record SilkyUIDiagnostic(int Start, int Length, string Message);

/// <summary>独立于 SDK 的源码导航目标。</summary>
internal sealed record SilkyUINavigationTarget(string SourceFilePath, int SourceLine, int SourceColumn);

internal sealed record SilkyUISymbolInfo(
    SilkyUISymbolKind Kind, int Start, int Length, string SymbolName, string CurrentTag,
    XmlMappingClass SilkyUiClass, SilkyUIProperty SilkyUiProperty, SilkyUIElementGroupClass BodyClass)
{
    public SilkyUINavigationTarget NavigationTarget => SilkyUiProperty != null
        ? new(SilkyUiProperty.SourceFilePath, SilkyUiProperty.SourceLine, SilkyUiProperty.SourceColumn)
        : SilkyUiClass == null ? null : new(SilkyUiClass.SourceFilePath, SilkyUiClass.SourceLine, SilkyUiClass.SourceColumn);
}

internal sealed record SilkyUIElementInfo(
    SilkyUIXmlTag Tag, bool IsKnown, XmlMappingClass MappingClass,
    SilkyUIElementGroupClass BodyClass, ImmutableList<SilkyUIProperty> Properties,
    SilkyUIProperty MemberProperty);

internal sealed record SilkyUIAttributeInfo(
    SilkyUIXmlAttribute Attribute, SilkyUISemanticAttributeKind Kind, SilkyUIProperty Property,
    SilkyUIAttributeKind DirectiveKind, bool HasKnownProperties);
