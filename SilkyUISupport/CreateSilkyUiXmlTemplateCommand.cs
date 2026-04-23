using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SilkyUISupport;

using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

/// <summary>
/// 在 C# 编辑器右键菜单中提供“创建 SilkyUI XML 初始模板”命令，
/// 并根据当前类名生成同目录下的 .sui.xml 文件。
/// </summary>
internal sealed class CreateSilkyUiXmlTemplateCommand
{
    private const int CommandId = 0x0100;
    private static readonly Guid CommandSet = new("a11c5b72-0ffb-4d12-9a86-7ad8fdf2c351");

    // 生成 Body Class 时需要保留完整命名空间，但不带 global:: 和泛型参数。
    private static readonly SymbolDisplayFormat ClassNameDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private readonly AsyncPackage _package;
    private readonly VisualStudioWorkspace _workspace;
    private readonly IVsEditorAdaptersFactoryService _adapterService;
    private readonly ITextDocumentFactoryService _textDocumentFactoryService;
    private readonly IVsTextManager _textManager;

    private CreateSilkyUiXmlTemplateCommand(
        AsyncPackage package,
        OleMenuCommandService commandService,
        VisualStudioWorkspace workspace,
        IVsEditorAdaptersFactoryService adapterService,
        ITextDocumentFactoryService textDocumentFactoryService,
        IVsTextManager textManager)
    {
        _package = package;
        _workspace = workspace;
        _adapterService = adapterService;
        _textDocumentFactoryService = textDocumentFactoryService;
        _textManager = textManager;

        var menuCommand = new OleMenuCommand(Execute, new CommandID(CommandSet, CommandId));
        menuCommand.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(menuCommand);
    }

    /// <summary>
    /// 解析命令所需的 VS 服务并完成菜单命令注册。
    /// </summary>
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var textManager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;

        Assumes.Present(commandService);
        Assumes.Present(componentModel);
        Assumes.Present(textManager);

        var workspace = componentModel.GetService<VisualStudioWorkspace>();
        var adapterService = componentModel.GetService<IVsEditorAdaptersFactoryService>();
        var textDocumentFactoryService = componentModel.GetService<ITextDocumentFactoryService>();

        Assumes.Present(workspace);
        Assumes.Present(adapterService);
        Assumes.Present(textDocumentFactoryService);

        _ = new CreateSilkyUiXmlTemplateCommand(
            package,
            commandService,
            workspace,
            adapterService,
            textDocumentFactoryService,
            textManager);
    }

    private void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (sender is not OleMenuCommand command)
            return;

        var isCSharpFile = false;

        try
        {
            isCSharpFile = ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                var context = await TryGetActiveCSharpContextAsync(CancellationToken.None).ConfigureAwait(false);
                return context != null;
            });
        }
        catch
        {
            isCSharpFile = false;
        }

        // 右键菜单的上下文判断过严时容易直接“不显示命令”，
        // 这里改为始终显示，仅在不是 C# 文件时禁用。
        command.Supported = true;
        command.Visible = true;
        command.Enabled = isCSharpFile;
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(
                    _package,
                    ex.Message,
                    "SilkyUI Support",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }).Task.Forget();
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var context = await TryGetCurrentClassContextAsync(cancellationToken).ConfigureAwait(false);
        if (context == null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VsShellUtilities.ShowMessageBox(
                _package,
                "当前光标不在当前解决方案的 C# 类名上。",
                "SilkyUI Support",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return;
        }

        var xmlFilePath = BuildXmlFilePath(context.SourceFilePath, context.ClassSymbol.Name);

        // 文件已存在时只打开，不覆盖用户内容。
        if (!File.Exists(xmlFilePath))
        {
            var xmlContent = BuildXmlContent(context.ClassSymbol);

            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xmlFilePath) ?? throw new InvalidOperationException("无法确定 XML 输出目录。"));
                File.WriteAllText(xmlFilePath, xmlContent, new UTF8Encoding(false));
            }, cancellationToken).ConfigureAwait(false);
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.OpenDocument(
            _package,
            xmlFilePath,
            Guid.Empty,
            out _,
            out _,
            out _,
            out _);
    }

    /// <summary>
    /// 基于当前活动 C# 文档和触发位置，解析出目标类符号及其源文件路径。
    /// </summary>
    private async Task<CurrentClassContext> TryGetCurrentClassContextAsync(CancellationToken cancellationToken)
    {
        var activeContext = await TryGetActiveCSharpContextAsync(cancellationToken).ConfigureAwait(false);
        if (activeContext == null)
            return null;

        if (!TryGetInvocationPosition(activeContext.TextView, activeContext.DocumentBuffer, out var triggerPoint))
            return null;

        var root = await activeContext.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await activeContext.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root == null || semanticModel == null)
            return null;

        var classSymbol = TryResolveClassSymbol(root, semanticModel, triggerPoint.Position, cancellationToken);
        if (classSymbol == null || !classSymbol.Locations.Any(location => location.IsInSource))
            return null;

        var sourceFilePath = classSymbol.Locations
            .FirstOrDefault(location => location.IsInSource)?
            .SourceTree?
            .FilePath;

        if (string.IsNullOrWhiteSpace(sourceFilePath))
            return null;

        return new CurrentClassContext(classSymbol, sourceFilePath);
    }

    /// <summary>
    /// 获取当前正在交互的 C# 编辑器上下文。
    /// </summary>
    private async Task<ActiveCSharpContext> TryGetActiveCSharpContextAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var activeView = default(IVsTextView);

        // 右键打开菜单时，编辑器焦点有时会变化，先取“必须有焦点”的视图，
        // 失败后再回退到普通活动视图。
        if (ErrorHandler.Failed(_textManager.GetActiveView(1, null, out activeView)) || activeView == null)
        {
            if (ErrorHandler.Failed(_textManager.GetActiveView(0, null, out activeView)) || activeView == null)
                return null;
        }

        if (activeView == null)
            return null;

        var textView = _adapterService.GetWpfTextView(activeView);
        if (textView == null)
            return null;

        var documentBuffer = textView.TextDataModel.DocumentBuffer;
        if (!_textDocumentFactoryService.TryGetTextDocument(documentBuffer, out var textDocument))
            return null;

        if (!textDocument.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var documentId = _workspace.CurrentSolution.GetDocumentIdsWithFilePath(textDocument.FilePath).FirstOrDefault();
        if (documentId == null)
            return null;

        var document = _workspace.CurrentSolution.GetDocument(documentId);
        if (document == null || document.Project.Language != LanguageNames.CSharp)
            return null;

        return new ActiveCSharpContext(textView, documentBuffer, document);
    }

    /// <summary>
    /// 尝试从给定位置解析类符号。
    /// 支持类声明、类型引用以及构造函数调用等几种常见场景。
    /// </summary>
    private static INamedTypeSymbol TryResolveClassSymbol(
        SyntaxNode root,
        SemanticModel semanticModel,
        int position,
        CancellationToken cancellationToken)
    {
        foreach (var candidatePosition in GetCandidatePositions(position, root.FullSpan.End))
        {
            var token = root.FindToken(candidatePosition);

            if (token.Parent is ClassDeclarationSyntax classDeclaration &&
                PositionTouchesSpan(classDeclaration.Identifier.Span, position))
            {
                var declaredSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken);
                if (declaredSymbol is INamedTypeSymbol declaredClassSymbol && declaredClassSymbol.TypeKind == TypeKind.Class)
                    return declaredClassSymbol;
            }

            var typeSyntax = token.Parent?
                .AncestorsAndSelf()
                .OfType<TypeSyntax>()
                .FirstOrDefault(syntax => PositionTouchesSpan(syntax.Span, position));

            if (typeSyntax != null)
            {
                var typeSymbol = semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type;
                if (typeSymbol is INamedTypeSymbol typeClassSymbol &&
                    typeClassSymbol.TypeKind == TypeKind.Class &&
                    string.Equals(token.ValueText, typeClassSymbol.Name, StringComparison.Ordinal))
                {
                    return typeClassSymbol;
                }
            }

            var nameSyntax = token.Parent?
                .AncestorsAndSelf()
                .OfType<NameSyntax>()
                .FirstOrDefault(syntax => PositionTouchesSpan(syntax.Span, position));

            if (nameSyntax != null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(nameSyntax, cancellationToken);
                var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

                if (symbol is INamedTypeSymbol namedTypeSymbol &&
                    namedTypeSymbol.TypeKind == TypeKind.Class &&
                    string.Equals(token.ValueText, namedTypeSymbol.Name, StringComparison.Ordinal))
                {
                    return namedTypeSymbol;
                }

                if (symbol is IMethodSymbol constructorSymbol &&
                    constructorSymbol.MethodKind == MethodKind.Constructor &&
                    constructorSymbol.ContainingType.TypeKind == TypeKind.Class &&
                    string.Equals(token.ValueText, constructorSymbol.ContainingType.Name, StringComparison.Ordinal))
                {
                    return constructorSymbol.ContainingType;
                }
            }
        }

        return null;
    }

    // VS 返回的位置可能恰好落在标识符边界上，因此额外检查前一个字符位置。
    private static int[] GetCandidatePositions(int position, int upperBoundExclusive)
    {
        if (upperBoundExclusive <= 0)
            return Array.Empty<int>();

        var current = Math.Max(0, Math.Min(position, upperBoundExclusive - 1));
        var previous = Math.Max(0, Math.Min(position - 1, upperBoundExclusive - 1));

        return current == previous
            ? new[] { current }
            : new[] { current, previous };
    }

    private static bool PositionTouchesSpan(RoslynTextSpan span, int position)
    {
        return span.Contains(position) || (position > 0 && span.Contains(position - 1)) || span.End == position;
    }

    /// <summary>
    /// 优先使用右键实际点击位置，其次回退到当前选区起点和光标位置。
    /// </summary>
    private static bool TryGetInvocationPosition(IWpfTextView textView, ITextBuffer documentBuffer, out SnapshotPoint point)
    {
        var snapshot = documentBuffer.CurrentSnapshot;

        if (CSharpContextMenuPositionState.TryGet(textView, snapshot, out point))
            return true;

        if (!textView.Selection.IsEmpty)
        {
            point = textView.Selection.Start.Position;
            return true;
        }

        var caretPoint = textView.Caret.Position.Point.GetPoint(documentBuffer, PositionAffinity.Successor);
        if (!caretPoint.HasValue)
        {
            point = default;
            return false;
        }

        point = caretPoint.Value;
        return true;
    }

    private static string BuildXmlFilePath(string sourceFilePath, string className)
    {
        var directory = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("无法确定类文件所在目录。");

        return Path.Combine(directory, className + ".sui.xml");
    }

    private static string BuildXmlContent(INamedTypeSymbol classSymbol)
    {
        var fullClassName = classSymbol.ToDisplayString(ClassNameDisplayFormat);

        return
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\r\n" +
            "<!-- Class 填写对应类名 -->\r\n" +
            "<Body Class=\"" + fullClassName + "\">\r\n" +
            "</Body>\r\n";
    }

    /// <summary>
    /// 创建 XML 模板所需的最小上下文。
    /// </summary>
    private sealed class CurrentClassContext
    {
        public CurrentClassContext(INamedTypeSymbol classSymbol, string sourceFilePath)
        {
            ClassSymbol = classSymbol;
            SourceFilePath = sourceFilePath;
        }

        public INamedTypeSymbol ClassSymbol { get; }

        public string SourceFilePath { get; }
    }

    /// <summary>
    /// 当前活动 C# 编辑器及其 Roslyn 文档上下文。
    /// </summary>
    private sealed class ActiveCSharpContext
    {
        public ActiveCSharpContext(IWpfTextView textView, ITextBuffer documentBuffer, Document document)
        {
            TextView = textView;
            DocumentBuffer = documentBuffer;
            Document = document;
        }

        public IWpfTextView TextView { get; }

        public ITextBuffer DocumentBuffer { get; }

        public Document Document { get; }
    }
}
