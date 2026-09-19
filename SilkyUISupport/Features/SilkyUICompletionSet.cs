using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

/// <summary>
/// 同时按补全项的显示文本和插入文本进行不区分大小写的匹配。
/// </summary>
internal sealed class SilkyUICompletionSet(
    string moniker,
    string displayName,
    ITrackingSpan applicableTo,
    IEnumerable<Completion> completions, ITrackingPoint filterEnd = null) : CompletionSet(moniker, displayName, applicableTo, completions, null)
{
    private readonly IReadOnlyList<Completion> _allCompletions = [.. completions];

    public override void Filter()
    {
        var snapshot = ApplicableTo.TextBuffer.CurrentSnapshot;
        var span = ApplicableTo.GetSpan(snapshot);
        var end = filterEnd == null ? span.End.Position
            : Math.Max(span.Start.Position, Math.Min(span.End.Position, filterEnd.GetPoint(snapshot).Position));
        var input = snapshot.GetText(Span.FromBounds(span.Start.Position, end));
        var matches = string.IsNullOrEmpty(input)
            ? _allCompletions
            : _allCompletions.Where(completion =>
                ContainsIgnoreCase(completion.DisplayText, input) ||
                ContainsIgnoreCase(completion.InsertionText, input));

        WritableCompletions.BeginBulkOperation();
        try
        {
            WritableCompletions.Clear();
            foreach (var completion in matches)
            {
                WritableCompletions.Add(completion);
            }
        }
        finally
        {
            WritableCompletions.EndBulkOperation();
        }

        if (WritableCompletions.Count > 0) SelectBestMatch();
    }

    private static bool ContainsIgnoreCase(string text, string value)
        => !string.IsNullOrEmpty(text) &&
           text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
