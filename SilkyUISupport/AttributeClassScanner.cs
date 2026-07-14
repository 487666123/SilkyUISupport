using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

namespace SilkyUISupport;

/// <summary>
/// 扫描C#项目中带有指定特性的类名，并提供缓存功能
/// </summary>
[Export(typeof(AttributeClassScanner))]
internal class AttributeClassScanner
{
    public async Task<List<XmlMappingClass>> GetClassesWithAttributeAsync(VisualStudioWorkspace workspace, string attributeName)
    {
        var xmlMappingClasses = new List<XmlMappingClass>();
        if (workspace?.CurrentSolution == null) return xmlMappingClasses;

        // 筛选出语言为 C# 的项目
        foreach (var project in GetCSharpProjects(workspace))
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

                // 可以有多个别名, 重复别名跳过

                var properties = GetPublicReadWriteProperties(cls).ToImmutableArray();
                foreach (var attr in attrs)
                {
                    if (attr.ConstructorArguments.Length == 0) continue;
                    var alias = attr.ConstructorArguments[0].Value as string;
                    if (string.IsNullOrWhiteSpace(alias)) continue;

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

    private const string UIElementGroupName = "SilkyUIFramework.Elements.UIElementGroup";

    /// <summary>
    /// 查找继承自 UIElementGroup 的 public 类（Body Class 属性的补全源）。
    /// </summary>
    public async Task<List<SilkyUIElementGroupClass>> GetUIElementGroupClassesAsync(VisualStudioWorkspace workspace)
    {
        var result = new List<SilkyUIElementGroupClass>();
        if (workspace?.CurrentSolution == null) return result;

        foreach (var project in GetCSharpProjects(workspace))
        {
            if (await project.GetCompilationAsync() is not { } compilation) continue;

            if (compilation.GetTypeByMetadataName(UIElementGroupName) is not { } elementGroupType) continue;

            var classes = GetAllPublicClass(compilation);

            foreach (var cls in classes)
            {
                if (!InheritsFrom(cls, elementGroupType)) continue;

                var properties = GetPublicReadWriteProperties(cls);
                result.Add(new SilkyUIElementGroupClass(cls.Name, cls.ToDisplayString(), [.. properties]));
            }
        }

        // 按全名去重
        var seen = new HashSet<string>();
        result.RemoveAll(c => !seen.Add(c.FullName));
        return result;
    }

    private static IEnumerable<Project> GetCSharpProjects(VisualStudioWorkspace workspace)
    {
        return workspace.CurrentSolution.Projects.Where(p => p.Language == LanguageNames.CSharp);
    }

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
    /// 获取类中所有公开的可读写属性（包含继承自父类的属性）
    /// </summary>
    /// <param name="cls">类符号</param>
    /// <returns>属性列表</returns>
    private List<SilkyUIProperty> GetPublicReadWriteProperties(INamedTypeSymbol cls)
    {
        var propertyDict = new Dictionary<string, SilkyUIProperty>();
        var currentType = cls;

        // 遍历当前类和所有基类，直到 object 类型
        while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
        {
            // 遍历当前类的所有公开可读写属性
            foreach (var property in currentType.GetMembers().OfType<IPropertySymbol>()
                                        .Where(p => p.DeclaredAccessibility == Accessibility.Public &&
                                                    p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public &&
                                                    p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public))
            {
                // 子类属性优先：如果属性名已存在（子类已定义同名属性），跳过父类的
                if (propertyDict.ContainsKey(property.Name))
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
