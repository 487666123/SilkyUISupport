using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(INavigableSymbolSourceProvider))]
[Name("SilkyUI XML navigable symbol source")]
[ContentType("SilkyUI XML")]
internal class SilkyUINavigableSymbolSourceProvider : INavigableSymbolSourceProvider
{
    [Import]
    internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    [Import]
    internal SVsServiceProvider ServiceProvider { get; set; } = null!;

    public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer)
    {
        return new SilkyUINavigableSymbolSource(buffer, MetadataService, ServiceProvider);
    }
}

internal sealed class SilkyUINavigableSymbolSource(
    ITextBuffer buffer,
    SilkyUIMetadataService metadataService,
    IServiceProvider serviceProvider) : INavigableSymbolSource
{
    private readonly ITextBuffer _buffer = buffer;
    private readonly SilkyUIMetadataService _metadataService = metadataService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public Task<INavigableSymbol> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken token)
    {
        if (triggerSpan.Snapshot.TextBuffer != _buffer)
            return Task.FromResult<INavigableSymbol>(null);

        var snapshot = triggerSpan.Snapshot;
        var position = triggerSpan.Start.Position;

        if (!SilkyUIXmlSymbolResolver.TryResolve(snapshot, position, _metadataService, out var resolution))
            return Task.FromResult<INavigableSymbol>(null);

        if (!TryGetNavigationTarget(resolution, out var target))
            return Task.FromResult<INavigableSymbol>(null);

        return Task.FromResult<INavigableSymbol>(
            new SilkyUINavigableSymbol(
                new SnapshotSpan(snapshot, target.SymbolSpan),
                target.SourceFilePath,
                target.SourceLine,
                target.SourceColumn,
                _serviceProvider));
    }

    public void Dispose() { }

    private static bool TryGetNavigationTarget(SilkyUISymbolResolution resolution, out SilkyUINavigationTarget target)
    {
        target = default;

        switch (resolution.Kind)
        {
            case SilkyUISymbolKind.Element:
                if (resolution.SilkyUiClass == null || string.IsNullOrWhiteSpace(resolution.SilkyUiClass.SourceFilePath))
                    return false;

                target = new SilkyUINavigationTarget(
                    resolution.SymbolSpan,
                    resolution.SilkyUiClass.SourceFilePath,
                    resolution.SilkyUiClass.SourceLine,
                    resolution.SilkyUiClass.SourceColumn);
                return true;
            case SilkyUISymbolKind.Attribute:
                if (resolution.SilkyUiProperty == null || string.IsNullOrWhiteSpace(resolution.SilkyUiProperty.SourceFilePath))
                    return false;

                target = new SilkyUINavigationTarget(
                    resolution.SymbolSpan,
                    resolution.SilkyUiProperty.SourceFilePath,
                    resolution.SilkyUiProperty.SourceLine,
                    resolution.SilkyUiProperty.SourceColumn);
                return true;
            default:
                return false;
        }
    }
}

internal readonly struct SilkyUINavigationTarget(Span symbolSpan, string sourceFilePath, int sourceLine, int sourceColumn)
{
    public Span SymbolSpan { get; } = symbolSpan;

    public string SourceFilePath { get; } = sourceFilePath;

    public int SourceLine { get; } = sourceLine;

    public int SourceColumn { get; } = sourceColumn;
}

internal sealed class SilkyUINavigableSymbol(
    SnapshotSpan symbolSpan,
    string sourceFilePath,
    int sourceLine,
    int sourceColumn,
    IServiceProvider serviceProvider) : INavigableSymbol
{
    private static readonly INavigableRelationship[] SupportedRelationships =
    [
        PredefinedNavigableRelationships.Definition
    ];

    private readonly string _sourceFilePath = sourceFilePath;
    private readonly int _sourceLine = sourceLine;
    private readonly int _sourceColumn = sourceColumn;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public SnapshotSpan SymbolSpan { get; } = symbolSpan;

    public IEnumerable<INavigableRelationship> Relationships => SupportedRelationships;

    public void Navigate(INavigableRelationship relationship)
    {
        if (relationship == null || relationship.Name != PredefinedNavigableRelationships.Definition.Name)
            return;

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            NavigateToSource();
        });
    }

    private void NavigateToSource()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        VsShellUtilities.OpenDocument(
            _serviceProvider,
            _sourceFilePath,
            Guid.Empty,
            out _,
            out _,
            out var windowFrame,
            out var textView);

        textView ??= VsShellUtilities.GetTextView(windowFrame);
        if (textView == null)
            return;

        textView.SetCaretPos(_sourceLine, _sourceColumn);
        textView.CenterLines(_sourceLine, 1);
    }
}
