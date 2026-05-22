using System.Collections.Immutable;

namespace SilkyUISupport;

internal class SilkyUIClass(
    ImmutableArray<SilkyUIProperty> silkyUIProperties,
    string name,
    string fullName,
    string sourceFilePath,
    int sourceLine,
    int sourceColumn)
{
    public string Name { get; } = name;
    public string FullName { get; } = fullName;
    public string SourceFilePath { get; } = sourceFilePath;
    public int SourceLine { get; } = sourceLine;
    public int SourceColumn { get; } = sourceColumn;
    public ImmutableArray<SilkyUIProperty> Properties { get; } = silkyUIProperties;
}

internal class SilkyUIProperty(
    string name,
    string typeName,
    string declaringTypeName,
    ImmutableArray<string> enums,
    string sourceFilePath,
    int sourceLine,
    int sourceColumn)
{
    public string Name { get; } = name;
    public string TypeName { get; } = typeName;
    public string DeclaringTypeName { get; } = declaringTypeName;
    public ImmutableArray<string> Enums { get; } = enums;
    public string SourceFilePath { get; } = sourceFilePath;
    public int SourceLine { get; } = sourceLine;
    public int SourceColumn { get; } = sourceColumn;
}

/// <summary>Body Class 属性补全用的轻量模型。</summary>
internal class SilkyUIElementGroupClass(string name, string fullName, ImmutableArray<SilkyUIProperty> properties)
{
    public string Name { get; } = name;
    public string FullName { get; } = fullName;
    public ImmutableArray<SilkyUIProperty> Properties { get; } = properties;
}
