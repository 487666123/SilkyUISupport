using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SilkyUISupport;

/// <summary>CLR XML 命名空间声明；前缀由 XML 作用域解析，此处只处理 URI。</summary>
internal sealed class SilkyUIClrNamespace
{
    public const string Prefix = "clr-namespace:";
    private SilkyUIClrNamespace(string namespaceName) => NamespaceName = namespaceName;

    public string NamespaceName { get; }

    public static bool IsClrNamespace(string uri)
        => uri != null && uri.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string uri, out SilkyUIClrNamespace declaration, out string error)
    {
        declaration = null;
        error = null;
        if (!IsClrNamespace(uri))
        {
            error = "CLR 命名空间必须以 clr-namespace: 开头。";
            return false;
        }
        if (uri.Any(char.IsWhiteSpace))
        {
            error = "CLR 命名空间声明不能包含空白字符。";
            return false;
        }

        var namespaceName = uri.Substring(Prefix.Length);
        if (namespaceName.IndexOf(';') >= 0)
        {
            error = "CLR 命名空间应为 clr-namespace:Namespace，不支持程序集参数。";
            return false;
        }

        // 元数据名称不含 C# 的 @ 转义；关键字本身仍是有效 CLR 名称。
        if (namespaceName.Length != 0 && namespaceName.Split('.').Any(part => !IsIdentifier(part)))
        {
            error = "CLR 命名空间必须由点分隔的有效标识符组成。";
            return false;
        }
        declaration = new SilkyUIClrNamespace(namespaceName);
        return true;
    }

    internal static bool IsIdentifier(string value)
        => !string.IsNullOrEmpty(value) && SyntaxFacts.IsIdentifierStartCharacter(value[0]) &&
           value.Skip(1).All(SyntaxFacts.IsIdentifierPartCharacter);
}

/// <summary>单个项目的编译及程序集类型索引，在刷新线程构建后只读使用。</summary>
internal sealed class SilkyUIClrProject
{
    private readonly ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>> _types;
    private readonly ImmutableArray<string> _namespaceNames;

    internal SilkyUIClrProject(Compilation compilation)
    {
        Compilation = compilation;
        var assemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
        foreach (var reference in compilation.References)
        {
            // 未显式设置别名的引用默认通过 global 可见，只有 extern alias 的引用不参与候选。
            if (!reference.Properties.Aliases.IsDefaultOrEmpty &&
                !reference.Properties.Aliases.Contains("global", StringComparer.Ordinal)) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                assemblies.Add(assembly);
        }

        var indexes = assemblies.Select(assembly => new AssemblyIndex(assembly)).ToArray();
        _types = indexes.SelectMany(index => index.Types)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key,
                group => group.SelectMany(pair => pair.Value).ToImmutableArray(), StringComparer.Ordinal);
        _namespaceNames = indexes.SelectMany(index => index.NamespaceNames)
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
    }

    public Compilation Compilation { get; }

    public IEnumerable<string> GetNamespaceNames() => _namespaceNames;

    public string ValidateNamespace(string uri)
    {
        SilkyUIClrNamespace.TryParse(uri, out _, out var error);
        return error;
    }

    public ImmutableArray<INamedTypeSymbol> GetTypes(string namespaceUri, ISymbol within, out string error)
    {
        if (!SilkyUIClrNamespace.TryParse(namespaceUri, out var declaration, out error)) return [];
        if (!_types.TryGetValue(declaration.NamespaceName, out var types)) return [];
        if (!TryGetBindingContext(within, out var semanticModel, out var position))
        {
            error = "当前项目尚无可用于 CLR 类型查找的 C# 上下文。";
            return [];
        }

        // 索引仅提供候选名称；实际类型由 C# 绑定，避免重复引用、类型转发及同名类型导致错误候选。
        var results = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var name in types.Where(type => type.Arity == 0).Select(type => type.Name)
                     .Where(SilkyUIClrNamespace.IsIdentifier).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            var fullName = GetFullName(declaration, name);
            var type = ResolveGlobalType(fullName, within, semanticModel, position, out _);
            if (type != null) results.Add(type);
        }
        return results.ToImmutable();
    }

    public INamedTypeSymbol ResolveType(string namespaceUri, string localName, ISymbol within, out string error)
    {
        if (!SilkyUIClrNamespace.TryParse(namespaceUri, out var declaration, out error)) return null;
        if (!SilkyUIClrNamespace.IsIdentifier(localName))
        {
            error = $"“{localName}”不是受支持的顶层 CLR 类型名称。";
            return null;
        }
        if (!TryGetBindingContext(within, out var semanticModel, out var position))
        {
            error = "当前项目尚无可用于 CLR 类型查找的 C# 上下文。";
            return null;
        }
        return ResolveGlobalType(GetFullName(declaration, localName), within, semanticModel, position, out error);
    }

    private static string GetFullName(SilkyUIClrNamespace declaration, string localName)
        => declaration.NamespaceName.Length == 0 ? localName : declaration.NamespaceName + "." + localName;

    private bool TryGetBindingContext(ISymbol within, out SemanticModel semanticModel, out int position)
    {
        semanticModel = null;
        position = 0;
        if (within is INamedTypeSymbol rootType)
        {
            // 与 Analyzer 选择相同的源码上下文；file 局部类型遮蔽时同样保守拒绝。
            var declaration = rootType.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
                .OfType<TypeDeclarationSyntax>().FirstOrDefault(syntax => !syntax.OpenBraceToken.IsMissing &&
                    syntax.OpenBraceToken.IsKind(SyntaxKind.OpenBraceToken));
            if (declaration == null) return false;
            semanticModel = Compilation.GetSemanticModel(declaration.SyntaxTree);
            position = declaration.OpenBraceToken.Span.End;
            return true;
        }

        // sui:Class 尚未填写时，仍提供项目级的预览候选；创建资格按程序集可访问性检查。
        var tree = Compilation.SyntaxTrees.FirstOrDefault();
        if (tree == null) return false;
        semanticModel = Compilation.GetSemanticModel(tree);
        return true;
    }

    private INamedTypeSymbol ResolveGlobalType(string fullName, ISymbol within, SemanticModel semanticModel,
        int position, out string error)
    {
        var syntax = SyntaxFactory.ParseTypeName("global::" + string.Join(".", fullName.Split('.').Select(part => "@" + part)));
        var info = syntax.ContainsDiagnostics ? default : semanticModel.GetSpeculativeSymbolInfo(position, syntax,
            SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (info.CandidateReason == CandidateReason.Ambiguous)
        {
            error = $"CLR 类型“global::{fullName}”存在歧义，请调整类型命名空间或项目引用。";
            return null;
        }
        if (info.Symbol is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error)
        {
            error = $"无法通过 global:: 解析 CLR 类型“{fullName}”，请检查类型名称、可访问性及项目引用。";
            return null;
        }
        error = GetCreationError(type, within);
        return error == null ? type : null;
    }

    private string GetCreationError(INamedTypeSymbol type, ISymbol within)
    {
        var name = type.ToDisplayString();
        if (!SilkyUIClrNamespace.IsIdentifier(type.Name))
            return $"类型“{name}”的元数据名称不能作为 XML CLR 类型标签。";
        if (type.TypeKind != TypeKind.Class || type.ContainingType != null || type.IsFileLocal || type.IsStatic || type.IsAbstract || type.Arity != 0)
            return $"类型“{name}”必须是非抽象、非静态、非泛型的普通顶层类。";
        within ??= Compilation.Assembly;
        if (!Compilation.IsSymbolAccessibleWithin(type, within))
            return $"类型“{name}”在当前生成位置不可访问。";
        var constructors = type.InstanceConstructors.Where(ctor => ctor.Parameters.Length == 0 &&
            IsConstructorAccessible(ctor, type, within)).ToArray();
        if (constructors.Length == 0)
            return $"类型“{name}”没有可用于 new 的可访问无参构造函数。";
        if (HasRequiredMembers(type) && !constructors.Any(ctor => ctor.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")))
            return $"类型“{name}”含 required 成员，但无参构造函数未声明 SetsRequiredMembers。";
        return null;
    }

    private bool IsConstructorAccessible(IMethodSymbol constructor, INamedTypeSymbol type, ISymbol within)
    {
        if (!Compilation.IsSymbolAccessibleWithin(constructor, within)) return false;
        // protected 构造函数允许派生构造链调用，但不能因此允许 new Base()。
        var insideType = false;
        for (var scope = within; scope != null; scope = scope.ContainingSymbol)
            if (SymbolEqualityComparer.Default.Equals(scope, type)) { insideType = true; break; }
        if (insideType) return true;
        if (constructor.DeclaredAccessibility == Accessibility.Protected ||
            constructor.DeclaredAccessibility == Accessibility.ProtectedAndInternal) return false;
        if (constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal)
            return SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Compilation.Assembly) ||
                   type.ContainingAssembly.GivesAccessTo(Compilation.Assembly);
        return true;
    }

    private static bool HasRequiredMembers(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } ||
                                                  member is IFieldSymbol { IsRequired: true })) return true;
        return false;
    }

    private sealed class AssemblyIndex
    {
        public AssemblyIndex(IAssemblySymbol assembly)
        {
            var types = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            Collect(assembly.GlobalNamespace, string.Empty, types, names);
            foreach (var type in assembly.GetForwardedTypes())
            {
                if (type.TypeKind == TypeKind.Error || type.ContainingType != null) continue;
                var namespaceName = GetNamespaceName(type.ContainingNamespace);
                Add(type, namespaceName, types);
                for (var name = namespaceName; ;)
                {
                    names.Add(name);
                    var dot = name.LastIndexOf('.');
                    if (dot < 0) break;
                    name = name.Substring(0, dot);
                }
            }
            Types = types.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
            NamespaceNames = names.OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
        }

        public ImmutableDictionary<string, ImmutableArray<INamedTypeSymbol>> Types { get; }
        public ImmutableArray<string> NamespaceNames { get; }

        private static void Collect(INamespaceSymbol scope, string name,
            Dictionary<string, List<INamedTypeSymbol>> types, HashSet<string> names)
        {
            names.Add(name);
            foreach (var type in scope.GetTypeMembers()) Add(type, name, types);
            foreach (var child in scope.GetNamespaceMembers())
                Collect(child, name.Length == 0 ? child.Name : name + "." + child.Name, types, names);
        }

        private static void Add(INamedTypeSymbol type, string name, Dictionary<string, List<INamedTypeSymbol>> types)
        {
            if (!types.TryGetValue(name, out var members)) types.Add(name, members = []);
            if (!members.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type))) members.Add(type);
        }

        private static string GetNamespaceName(INamespaceSymbol scope)
            => scope == null || scope.IsGlobalNamespace ? string.Empty :
                scope.ContainingNamespace.IsGlobalNamespace ? scope.Name : GetNamespaceName(scope.ContainingNamespace) + "." + scope.Name;
    }
}

/// <summary>XML 文件到所属项目的不可变索引。不可用的项目也参与归属判断，避免错误回退。</summary>
internal sealed class SilkyUIClrProjectIndex
{
    public static SilkyUIClrProjectIndex Empty { get; } = new([]);
    private readonly ImmutableArray<ProjectEntry> _entries;
    private SilkyUIClrProjectIndex(ImmutableArray<ProjectEntry> entries) => _entries = entries;

    public static async Task<SilkyUIClrProjectIndex> CreateAsync(Solution solution)
    {
        var entries = ImmutableArray.CreateBuilder<ProjectEntry>();
        foreach (var project in solution.Projects)
        {
            SilkyUIClrProject clrProject = null;
            if (project.Language == LanguageNames.CSharp && await project.GetCompilationAsync().ConfigureAwait(false) is { } compilation)
                clrProject = new SilkyUIClrProject(compilation);
            var documents = project.Documents.Select(document => NormalizePath(document.FilePath))
                .Concat(project.AdditionalDocuments.Select(document => NormalizePath(document.FilePath)))
                .Where(path => path != null).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            var projectPath = NormalizePath(project.FilePath);
            var directory = projectPath == null ? null : Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(directory))
                directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            else directory = null;
            entries.Add(new ProjectEntry(documents, directory, clrProject));
        }
        return new SilkyUIClrProjectIndex(entries.ToImmutable());
    }

    public SilkyUIClrProject GetProject(string filePath)
    {
        var path = NormalizePath(filePath);
        if (path == null) return null;
        var explicitMatches = _entries.Where(entry => entry.Documents.Contains(path)).ToArray();
        if (explicitMatches.Length != 0) return explicitMatches.Length == 1 ? explicitMatches[0].Project : null;
        ProjectEntry best = null;
        var ambiguous = false;
        foreach (var entry in _entries)
        {
            if (entry.Directory == null || !path.StartsWith(entry.Directory, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || entry.Directory.Length > best.Directory.Length)
            {
                best = entry;
                ambiguous = false;
            }
            else if (entry.Directory.Length == best.Directory.Length) ambiguous = true;
        }
        return ambiguous ? null : best?.Project;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private sealed class ProjectEntry(ImmutableHashSet<string> documents, string directory, SilkyUIClrProject project)
    {
        public ImmutableHashSet<string> Documents { get; } = documents;
        public string Directory { get; } = directory;
        public SilkyUIClrProject Project { get; } = project;
    }
}
