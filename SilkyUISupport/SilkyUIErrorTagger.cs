using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Export(typeof(ITaggerProvider))]
[Name("SilkyUI XML error tagger")]
[ContentType("SilkyUI XML")]
[TagType(typeof(IErrorTag))]
internal class SilkyUIErrorTaggerProvider : ITaggerProvider
{
    [Import]
    internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) == typeof(IErrorTag))
            return (ITagger<T>)(object)buffer.Properties.GetOrCreateSingletonProperty(
                () => new SilkyUIErrorTagger(buffer, MetadataService));
        return null;
    }
}

internal sealed class SilkyUIErrorTagger : ITagger<IErrorTag>
{
    private readonly ITextBuffer _buffer;
    private readonly SilkyUIMetadataService _metadataService;

    public SilkyUIErrorTagger(ITextBuffer buffer, SilkyUIMetadataService metadataService)
    {
        _buffer = buffer;
        _metadataService = metadataService;
        _buffer.Changed += OnBufferChanged;
        _metadataService.Refreshed += OnMetadataRefreshed;
    }

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
            new SnapshotSpan(e.After, 0, e.After.Length)));
    }

    private void OnMetadataRefreshed()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(snapshot, 0, snapshot.Length)));
        });
    }

    public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var text = snapshot.GetText();
        int pos = 0;

        while (pos < text.Length)
        {
            int tagStart = text.IndexOf('<', pos);
            if (tagStart < 0) break;
            if (tagStart + 1 >= text.Length) break;

            char next = text[tagStart + 1];

            // 跳过 XML 声明 <?xml ... ?> 和注释 <!-- ... -->
            if (next is '?' or '!')
            {
                string terminator = next == '?' ? "?>" : "-->";
                int end = text.IndexOf(terminator, tagStart + 2);
                if (end < 0) break;
                pos = end + terminator.Length;
                continue;
            }

            // 跳过闭合标签 </tag>
            if (next == '/')
            {
                int end = text.IndexOf('>', tagStart + 2);
                if (end < 0) break;
                pos = end + 1;
                continue;
            }

            // 找标签结束 >
            int tagEnd = text.IndexOf('>', tagStart + 1);
            if (tagEnd < 0) break;

            // 提取标签内部内容（不含 < 和 >）
            var inner = text.Substring(tagStart + 1, tagEnd - tagStart - 1);

            // 处理自闭合标签 <tag ... />
            bool selfClose = inner[inner.Length - 1] == '/';
            if (selfClose)
                inner = inner.Substring(0, inner.Length - 1).TrimEnd();

            // 提取标签名（第一个连续的单词）
            int nameLen = 0;
            while (nameLen < inner.Length && !char.IsWhiteSpace(inner[nameLen]))
                nameLen++;

            var tagName = inner.Substring(0, nameLen);
            if (tagName.Length == 0) { pos = tagEnd + 1; continue; }

            // Style、M. 前缀和 Style. 是框架特殊元素，跳过校验
            if (tagName is "Style" || tagName.StartsWith("M.") || tagName.StartsWith("Style."))
            {
                pos = tagEnd + 1;
                continue;
            }

            // 检查元素是否存在（Body 通过 Class 引用类，自身无需对应）
            var suiClass = tagName != "Body" ? _metadataService.GetClassByName(tagName) : null;
            if (tagName != "Body" && suiClass == null)
            {
                yield return MakeError(snapshot,
                    new Span(tagStart + 1, tagName.Length),
                    PredefinedErrorTypeNames.SyntaxError,
                    $"未知元素 '{tagName}'");
            }

            // 解析并校验属性
            if (nameLen < inner.Length)
            {
                foreach (var error in ParseAndValidateAttributes(
                    inner.Substring(nameLen), tagStart + 1 + nameLen,
                    snapshot, tagName, suiClass, _metadataService))
                {
                    yield return error;
                }
            }

            pos = tagEnd + 1;
        }
    }

    private static TagSpan<IErrorTag> MakeError(ITextSnapshot snapshot, Span span, string type, string message)
        => new(new SnapshotSpan(snapshot, span), new ErrorTag(type, message));

    private static bool IsSpecialAttribute(string name)
        => name is "Name" or "Class" or "Style" || name.StartsWith("Bind.");

    private static IEnumerable<TagSpan<IErrorTag>> ParseAndValidateAttributes(
        string section, int offset, ITextSnapshot snapshot,
        string tagName, SilkyUIClass? suiClass, SilkyUIMetadataService metadata)
    {
        var seen = new HashSet<string>();
        int i = 0;

        while (i < section.Length)
        {
            // 跳过空白
            while (i < section.Length && char.IsWhiteSpace(section[i])) i++;
            if (i >= section.Length) break;

            // 属性名
            int nameStart = i;
            while (i < section.Length && (char.IsLetterOrDigit(section[i]) || section[i] is '.' or '_' or '-'))
                i++;
            if (nameStart == i) { i++; continue; }

            var attrName = section.Substring(nameStart, i - nameStart);
            var attrNameSpan = new Span(offset + nameStart, attrName.Length);

            // 解析 = 和属性值（总是执行，确保 i 推进到整个属性结构之后）
            while (i < section.Length && char.IsWhiteSpace(section[i])) i++;
            bool hasEquals = i < section.Length && section[i] == '=';
            int valStart = -1, valLength = -1;
            string? valStr = null;

            if (hasEquals)
            {
                i++;
                while (i < section.Length && char.IsWhiteSpace(section[i])) i++;
                if (i < section.Length && section[i] is '"' or '\'')
                {
                    char quote = section[i];
                    i++;
                    valStart = i;
                    while (i < section.Length && section[i] != quote) i++;
                    valLength = i - valStart;
                    valStr = section.Substring(valStart, valLength);
                    if (i < section.Length) i++;
                }
            }

            // Body 的 Class 属性值校验
            if (tagName == "Body" && attrName == "Class" && valStr != null && valStr.Length > 0)
            {
                if (!metadata.GetUIClasses().Any(c => c.FullName == valStr))
                {
                    yield return MakeError(snapshot, new Span(offset + valStart, valLength),
                        PredefinedErrorTypeNames.SyntaxError, $"未知类 '{valStr}'");
                }
                continue;
            }

            // 框架特殊属性（Name、Class、Style、Bind.*），跳过校验
            if (IsSpecialAttribute(attrName))
                continue;

            // 重复属性检测
            if (!seen.Add(attrName))
            {
                yield return MakeError(snapshot, attrNameSpan,
                    PredefinedErrorTypeNames.SyntaxError, $"重复属性 '{attrName}'");
                continue;
            }

            // 校验属性名
            if (suiClass != null)
            {
                var prop = metadata.GetPropertyByName(tagName, attrName);
                if (prop == null)
                {
                    yield return MakeError(snapshot, attrNameSpan,
                        PredefinedErrorTypeNames.SyntaxError, $"'{tagName}' 上没有 '{attrName}' 属性");
                }
            }

            // 校验枚举属性值
            if (suiClass != null && valStr != null && valStr.Length > 0)
            {
                var prop = metadata.GetPropertyByName(tagName, attrName);
                if (prop != null && prop.Enums.Length > 0 && !prop.Enums.Contains(valStr))
                {
                    var preview = string.Join(", ", prop.Enums);
                    yield return MakeError(snapshot, new Span(offset + valStart, valLength),
                        PredefinedErrorTypeNames.SyntaxError, $"'{attrName}' 可选值: {preview}");
                }
            }
        }
    }
}
