using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SilkyUISupport;

/// <summary>语义请求使用的全部 C# 元数据的不可变视图。</summary>
internal sealed class SilkyUIMetadataSnapshot
{
    public static SilkyUIMetadataSnapshot Empty { get; } = new([], [], [], [],
        ImmutableDictionary<string, ImmutableList<SilkyUIProperty>>.Empty);

    private readonly SilkyUIClrProjectIndex _clrProjects;
    private readonly ImmutableDictionary<string, XmlMappingClass> _classesByAlias;
    private readonly ImmutableDictionary<string, SilkyUIElementGroupClass> _groupsByFullName;
    private readonly ImmutableDictionary<string, SilkyUIElementGroupClass> _groupsByName;

    public SilkyUIMetadataSnapshot(
        ImmutableList<XmlMappingClass> classes,
        ImmutableList<SilkyUIElementGroupClass> groupClasses,
        ImmutableList<SilkyUIProperty> styleProperties,
        ImmutableList<SilkyUITargetClass> targetClasses,
        ImmutableDictionary<string, ImmutableList<SilkyUIProperty>> targetProperties,
        SilkyUIClrProjectIndex clrProjects = null)
    {
        _clrProjects = clrProjects ?? SilkyUIClrProjectIndex.Empty;
        Classes = classes;
        GroupClasses = groupClasses;
        StyleProperties = styleProperties;
        TargetClasses = targetClasses;
        TargetProperties = targetProperties;
        _classesByAlias = CreateFirstMatchIndex(classes, static item => item.Alias);
        _groupsByFullName = CreateFirstMatchIndex(groupClasses, static item => item.FullName);
        _groupsByName = CreateFirstMatchIndex(groupClasses, static item => item.Name);
    }

    public ImmutableList<XmlMappingClass> Classes { get; }
    public ImmutableList<SilkyUIElementGroupClass> GroupClasses { get; }
    public ImmutableList<SilkyUIProperty> StyleProperties { get; }
    public ImmutableList<SilkyUITargetClass> TargetClasses { get; }
    private ImmutableDictionary<string, ImmutableList<SilkyUIProperty>> TargetProperties { get; }

    public SilkyUIClrProject GetClrProject(string filePath) => _clrProjects.GetProject(filePath);

    public XmlMappingClass GetClassByName(string className)
        => string.IsNullOrWhiteSpace(className) || !_classesByAlias.TryGetValue(className, out var mapped)
            ? null
            : mapped;

    public SilkyUIElementGroupClass GetGroupClassByName(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        if (_groupsByFullName.TryGetValue(className, out var group)) return group;
        return _groupsByName.TryGetValue(className, out group) ? group : null;
    }

    public ImmutableList<SilkyUIProperty> GetTargetProperties(string fullName)
        => string.IsNullOrWhiteSpace(fullName) || !TargetProperties.TryGetValue(fullName, out var properties)
            ? []
            : properties;

    private static ImmutableDictionary<string, T> CreateFirstMatchIndex<T>(IEnumerable<T> items, Func<T, string> getName)
    {
        var index = ImmutableDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var name = getName(item);
            // Preserve the first match and ignore names that queries already reject.
            if (!string.IsNullOrWhiteSpace(name) && !index.ContainsKey(name))
                index.Add(name, item);
        }
        return index.ToImmutable();
    }
}
