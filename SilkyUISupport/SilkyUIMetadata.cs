using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SilkyUISupport;

internal sealed record class XmlMappingClass(
    INamedTypeSymbol Class,
    ImmutableArray<SilkyUIProperty> Properties,
    string Alias,
    string SourceFilePath,
    int SourceLine,
    int SourceColumn);

internal sealed record class SilkyUIProperty(
    IPropertySymbol Property,
    ImmutableArray<string> Enums,
    string SourceFilePath,
    int SourceLine,
    int SourceColumn);

/// <summary>Body Class 属性补全用的轻量模型。</summary>
internal sealed record class SilkyUIElementGroupClass(
    string Name,
    string FullName,
    ImmutableArray<SilkyUIProperty> Properties);
