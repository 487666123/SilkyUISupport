using System;
using System.Collections.Immutable;
using System.Linq;

namespace SilkyUISupport;

/// <summary>语义请求使用的全部 C# 元数据的不可变视图。</summary>
internal sealed class SilkyUIMetadataSnapshot
{
    public static SilkyUIMetadataSnapshot Empty { get; } = new([], [], [], [],
        ImmutableDictionary<string, ImmutableList<SilkyUIProperty>>.Empty);

    public SilkyUIMetadataSnapshot(
        ImmutableList<XmlMappingClass> classes,
        ImmutableList<SilkyUIElementGroupClass> groupClasses,
        ImmutableList<SilkyUIProperty> styleProperties,
        ImmutableList<SilkyUITargetClass> targetClasses,
        ImmutableDictionary<string, ImmutableList<SilkyUIProperty>> targetProperties)
    {
        Classes = classes;
        GroupClasses = groupClasses;
        StyleProperties = styleProperties;
        TargetClasses = targetClasses;
        TargetProperties = targetProperties;
    }

    public ImmutableList<XmlMappingClass> Classes { get; }
    public ImmutableList<SilkyUIElementGroupClass> GroupClasses { get; }
    public ImmutableList<SilkyUIProperty> StyleProperties { get; }
    public ImmutableList<SilkyUITargetClass> TargetClasses { get; }
    private ImmutableDictionary<string, ImmutableList<SilkyUIProperty>> TargetProperties { get; }

    public XmlMappingClass GetClassByName(string className)
        => string.IsNullOrWhiteSpace(className)
            ? null
            : Classes.FirstOrDefault(item => item.Alias == className);

    public SilkyUIElementGroupClass GetGroupClassByName(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        return GroupClasses.FirstOrDefault(item => item.FullName == className)
            ?? GroupClasses.FirstOrDefault(item => item.Name == className);
    }

    public ImmutableList<SilkyUIProperty> GetTargetProperties(string fullName)
        => string.IsNullOrWhiteSpace(fullName) || !TargetProperties.TryGetValue(fullName, out var properties)
            ? []
            : properties;
}
