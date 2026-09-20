using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Shell;

namespace SilkyUISupport;

/// <summary>
/// 扫描C#项目中带有指定特性的类名，并提供缓存功能
/// </summary>
[Export(typeof(AttributeClassScanner))]
internal class AttributeClassScanner
{
    public async Task<List<XmlMappingClass>> GetClassesWithAttributeAsync(Solution solution, string attributeName)
    {
        var xmlMappingClasses = new List<XmlMappingClass>();
        if (solution == null) return xmlMappingClasses;

        // 筛选出语言为 C# 的项目
        foreach (var project in GetCSharpProjects(solution))
        {
            // 获取项目的编译结果（包含所有符号信息）
            if (await project.GetCompilationAsync() is not { } compilation) continue;

            // 检查是否存在指定类型
            if (compilation.GetTypeByMetadataName(attributeName) is not { } xmlMappingAttributeType) continue;

            // 找到所有公开类
            var classes = GetAllPublicClass(compilation);

            foreach (var cls in classes)
            {
                var attrs = cls.GetAttributes().Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, xmlMappingAttributeType));

                var sourceLocation = cls.Locations.FirstOrDefault(location => location.IsInSource);
                var lineSpan = sourceLocation?.GetLineSpan();

                var sourceFilePath = lineSpan?.Path ?? string.Empty;
                var sourceLine = lineSpan?.StartLinePosition.Line ?? 0;
                var sourceColumn = lineSpan?.StartLinePosition.Character ?? 0;

                // Collect properties once, when the first valid alias needs them.
                ImmutableArray<SilkyUIProperty> properties = default;
                foreach (var attr in attrs)
                {
                    if (attr.ConstructorArguments.Length == 0) continue;
                    var alias = attr.ConstructorArguments[0].Value as string;
                    if (string.IsNullOrWhiteSpace(alias)) continue;

                    if (properties.IsDefault)
                        properties = GetPublicProperties(cls).ToImmutableArray();

                    xmlMappingClasses.Add(new XmlMappingClass(
                        cls,
                        properties,
                        alias,
                        sourceFilePath,
                        sourceLine,
                        sourceColumn));
                }
            }
        }

        return xmlMappingClasses;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllPublicClass(Compilation compilation)
    {
        return compilation
            .GetSymbolsWithName(_ => true, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .Where(t => t.TypeKind == TypeKind.Class && t.DeclaredAccessibility == Accessibility.Public);
    }

    /// <summary>获取当前解决方案 C# 项目中的所有公开类，供 sui:Target 补全使用。</summary>
    public async Task<List<SilkyUITargetClass>> GetAllPublicClassesAsync(Solution solution)
    {
        var result = new List<SilkyUITargetClass>();
        if (solution == null) return result;

        foreach (var project in GetCSharpProjects(solution))
        {
            if (await project.GetCompilationAsync() is not { } compilation) continue;

            foreach (var cls in GetAllPublicClass(compilation))
                result.Add(new SilkyUITargetClass(cls, cls.ToDisplayString()));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        result.RemoveAll(target => !seen.Add(target.FullName));
        return result;
    }

    private const string UIElementGroupName = "SilkyUIFramework.Elements.UIElementGroup";

    /// <summary>
    /// 查找继承自 UIElementGroup 的 public 类（Body Class 属性的补全源）。
    /// </summary>
    public async Task<List<SilkyUIElementGroupClass>> GetUIElementGroupClassesAsync(Solution solution)
    {
        var result = new List<SilkyUIElementGroupClass>();
        if (solution == null) return result;

        foreach (var project in GetCSharpProjects(solution))
        {
            if (await project.GetCompilationAsync() is not { } compilation) continue;

            if (compilation.GetTypeByMetadataName(UIElementGroupName) is not { } elementGroupType) continue;

            var classes = GetAllPublicClass(compilation);

            foreach (var cls in classes)
            {
                if (!InheritsFrom(cls, elementGroupType)) continue;

                var properties = GetPublicProperties(cls);
                var sourceLocation = cls.Locations.FirstOrDefault(location => location.IsInSource);
                var lineSpan = sourceLocation?.GetLineSpan();
                result.Add(new SilkyUIElementGroupClass(cls.Name, cls.ToDisplayString(), [.. properties],
                    lineSpan?.Path ?? string.Empty, lineSpan?.StartLinePosition.Line ?? 0,
                    lineSpan?.StartLinePosition.Character ?? 0));
            }
        }

        // 按全名去重
        var seen = new HashSet<string>();
        result.RemoveAll(c => !seen.Add(c.FullName));
        return result;
    }

    private static IEnumerable<Project> GetCSharpProjects(Solution solution)
        => solution.Projects.Where(project => project.Language == LanguageNames.CSharp);

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// 获取类中公开可读或可写的实例非索引属性（包含继承自父类的属性）。
    /// Getter 和 setter 是否可用由具体使用场景进一步判断。
    /// </summary>
    /// <param name="cls">类符号</param>
    /// <returns>属性列表</returns>
    internal List<SilkyUIProperty> GetPublicProperties(INamedTypeSymbol cls)
    {
        var propertyDict = new Dictionary<string, SilkyUIProperty>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var currentType = cls;

        // 遍历当前类和所有基类，直到 object 类型
        while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
        {
            foreach (var property in currentType.GetMembers().OfType<IPropertySymbol>())
            {
                // 子类属性即使不适用于 XML，也会隐藏父类的同名属性。
                if (!seenNames.Add(property.Name))
                    continue;

                if (property.IsStatic || property.IsIndexer || property.Parameters.Length != 0 ||
                    property.DeclaredAccessibility != Accessibility.Public ||
                    (property.GetMethod?.DeclaredAccessibility != Accessibility.Public &&
                     property.SetMethod?.DeclaredAccessibility != Accessibility.Public))
                    continue;

                ImmutableArray<string> enumValues = [];
                var sourceLocation = property.Locations.FirstOrDefault(location => location.IsInSource);
                var lineSpan = sourceLocation?.GetLineSpan();
                var sourceFilePath = lineSpan?.Path ?? string.Empty;
                var sourceLine = lineSpan?.StartLinePosition.Line ?? 0;
                var sourceColumn = lineSpan?.StartLinePosition.Character ?? 0;

                // 如果属性类型是枚举，获取所有公开的枚举值
                if (property.Type.TypeKind == TypeKind.Enum && property.Type is INamedTypeSymbol enumType)
                {
                    enumValues = [.. enumType.GetMembers()
                                        .OfType<IFieldSymbol>()
                                        .Where(f => f.IsStatic && f.IsConst && f.DeclaredAccessibility == Accessibility.Public)
                                        .Select(f => f.Name)];
                }

                propertyDict[property.Name] = new SilkyUIProperty(
                    property,
                    enumValues,
                    sourceFilePath,
                    sourceLine,
                    sourceColumn);
            }

            // 继续处理父类
            currentType = currentType.BaseType;
        }

        return [.. propertyDict.Values];
    }
}
