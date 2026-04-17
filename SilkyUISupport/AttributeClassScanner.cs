using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;

namespace SilkyUISupport;

/// <summary>
/// 扫描C#项目中带有指定特性的类名，并提供缓存功能
/// </summary>
[Export(typeof(AttributeClassScanner))]
internal class AttributeClassScanner
{
    [Import]
    public VisualStudioWorkspace Workspace { get; set; }

    public List<SilkyUIClass> GetClassesWithAttribute(string targetAttributeName)
    {
        var suiClass = new List<SilkyUIClass>();
        if (Workspace?.CurrentSolution == null) return suiClass;

        // 遍历解决方案中的所有C#项目
        foreach (var project in Workspace.CurrentSolution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            // 获取项目的编译结果（包含所有符号信息）
            if (project.GetCompilationAsync().Result is not { } compilation) continue;

            // 先检查项目是否引用了SilkyUI：尝试查找目标Attribute类型
            var attributeType = compilation.GetTypeByMetadataName(targetAttributeName);
            if (attributeType == null) continue;

            // 查找所有类类型的符号
            var classes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>()
                                    .Where(t => t.TypeKind == TypeKind.Class && t.DeclaredAccessibility == Accessibility.Public);

            foreach (var cls in classes)
            {
                var attrs = cls.GetAttributes().Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType));

                foreach (var attr in attrs)
                {
                    var properties = GetPublicReadWriteProperties(cls);
                    suiClass.Add(new SilkyUIClass([.. properties], attr.ConstructorArguments[0].Value as string, cls.ToDisplayString()));
                }
            }
        }

        // 按完整类名去重
        return suiClass;
    }

    /// <summary>
    /// 获取类中所有公开的可读写属性
    /// </summary>
    /// <param name="cls">类符号</param>
    /// <returns>属性列表</returns>
    private List<SilkyUIProperty> GetPublicReadWriteProperties(INamedTypeSymbol cls)
    {
        var properties = new List<SilkyUIProperty>();

        // 遍历类的所有公开属性（包含继承的属性）
        foreach (var property in cls.GetMembers().OfType<IPropertySymbol>()
                                    .Where(p => p.DeclaredAccessibility == Accessibility.Public &&
                                                p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public &&
                                                p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public))
        {
            ImmutableArray<string> enumValues = ImmutableArray<string>.Empty;

            // 如果属性类型是枚举，获取所有公开的枚举值
            if (property.Type.TypeKind == TypeKind.Enum && property.Type is INamedTypeSymbol enumType)
            {
                enumValues = [.. enumType.GetMembers()
                                    .OfType<IFieldSymbol>()
                                    .Where(f => f.IsStatic && f.IsConst && f.DeclaredAccessibility == Accessibility.Public)
                                    .Select(f => f.Name)];
            }

            properties.Add(new SilkyUIProperty(property.Name, enumValues));
        }

        return properties;
    }
}

public class SilkyUIClass(ImmutableArray<SilkyUIProperty> silkyUIProperties, string name, string fullName)
{
    public string Name { get; } = name;
    public string FullName { get; } = fullName;
    public ImmutableArray<SilkyUIProperty> Properties { get; } = silkyUIProperties;
}

public class SilkyUIProperty(string name, ImmutableArray<string> enums)
{
    public string Name { get; } = name;

    public ImmutableArray<string> Enums = enums;
}