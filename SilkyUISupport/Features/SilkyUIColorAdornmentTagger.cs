using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using WinFormsColor = System.Drawing.Color;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;

namespace SilkyUISupport;

/// <summary>为 XNA Color 类型的属性值显示可点击的颜色图标。</summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType("SilkyUI XML")]
[TagType(typeof(IntraTextAdornmentTag))]
internal sealed class SilkyUIColorAdornmentTaggerProvider : IViewTaggerProvider
{
    [Import] internal SilkyUIMetadataService MetadataService { get; set; } = null!;

    public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView == null || buffer == null || textView.TextBuffer != buffer)
            return null;

        return buffer.Properties.GetOrCreateSingletonProperty(
            () => new SilkyUIColorAdornmentTagger(buffer, MetadataService)) as ITagger<T>;
    }
}

internal sealed class SilkyUIColorAdornmentTagger : ITagger<IntraTextAdornmentTag>
{
    private readonly ITextBuffer _buffer;
    private readonly SilkyUIMetadataService _metadataService;

    public SilkyUIColorAdornmentTagger(ITextBuffer buffer, SilkyUIMetadataService metadataService)
    {
        _buffer = buffer;
        _metadataService = metadataService;
        _buffer.Changed += OnBufferChanged;
        SilkyUIMetadataSubscription.Subscribe(_metadataService, this, static tagger => tagger.OnMetadataRefreshed());
    }

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));

    private void OnMetadataRefreshed()
    {
        var snapshot = _buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var document = SilkyUIXmlDocument.Get(snapshot);
        var model = new SilkyUISemanticModel(document, _metadataService.GetSnapshot());

        foreach (var tag in document.GetTags(spans))
        {
            if (tag.IsClosing || tag.Attributes.Count == 0) continue;

            foreach (var attribute in tag.Attributes)
            {
                if (attribute.ValueStart < 0 || !attribute.ValueComplete || string.IsNullOrWhiteSpace(attribute.Value))
                    continue;

                // 在语义解析和创建控件之前筛选实际请求的插入位置。
                var adornmentSpan = new SnapshotSpan(snapshot, attribute.ValueStart, 0);
                if (!spans.IntersectsWith(adornmentSpan)) continue;

                var resolved = model.ResolveAttribute(tag, attribute);
                if (resolved.Kind != SilkyUISemanticAttributeKind.Property || resolved.Property == null)
                    continue;

                var propertyType = resolved.Property.Property.Type.ToDisplayString();
                if (propertyType != "Microsoft.Xna.Framework.Color")
                    continue;

                if (!XnaColorParser.TryParse(attribute.Value, out var alpha, out var red, out var green, out var blue))
                    continue;

                var valueSpan = new SnapshotSpan(snapshot, attribute.ValueStart, attribute.Value.Length);
                var colorBlock = CreateColorBlock(alpha, red, green, blue, valueSpan, _buffer);

                var adornmentTag = new IntraTextAdornmentTag(colorBlock, null);
                yield return new TagSpan<IntraTextAdornmentTag>(adornmentSpan, adornmentTag);
            }
        }
    }

    private static UIElement CreateColorBlock(byte alpha, byte red, byte green, byte blue, 
        SnapshotSpan valueSpan, ITextBuffer buffer)
    {
        var color = Color.FromArgb(alpha, red, green, blue);

        var border = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 4, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            ToolTip = $"RGBA({red}, {green}, {blue}, {alpha / 255.0:F2})\n点击选择颜色"
        };

        border.MouseLeftButtonDown += (sender, e) =>
        {
            e.Handled = true;
            OpenColorPicker(color, valueSpan, buffer);
        };

        return border;
    }

    private static void OpenColorPicker(Color currentColor, SnapshotSpan valueSpan, ITextBuffer buffer)
    {
        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // 使用包含边界的跟踪范围，检测值内及边界处的修改，避免覆盖新内容。
                var originalValue = valueSpan.GetText();
                var trackingSpan = valueSpan.Snapshot.CreateTrackingSpan(valueSpan.Span, SpanTrackingMode.EdgeInclusive);
                if (!string.Equals(trackingSpan.GetText(buffer.CurrentSnapshot), originalValue, StringComparison.Ordinal))
                {
                    ActivityLog.LogWarning(nameof(SilkyUIColorAdornmentTagger), "颜色值已变化，取消打开选择器。");
                    return;
                }

                using (var dialog = new WinFormsColorDialog())
                {
                    // 设置当前颜色
                    dialog.Color = WinFormsColor.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B);
                    dialog.FullOpen = true; // 显示完整颜色选择器
                    dialog.AnyColor = true;

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var newColor = dialog.Color;
                        var newA = currentColor.A; // 保持原 Alpha（ColorDialog 不支持 Alpha）

                        var newColorString = newA == 255
                            ? $"#{newColor.R:X2}{newColor.G:X2}{newColor.B:X2}"
                            : $"#{newColor.R:X2}{newColor.G:X2}{newColor.B:X2}{newA:X2}";

                        // 以编辑会话的快照定位；目标被删除或修改后不再回写。
                        using (var edit = buffer.CreateEdit())
                        {
                            var currentSpan = trackingSpan.GetSpan(edit.Snapshot);
                            if (!string.Equals(currentSpan.GetText(), originalValue, StringComparison.Ordinal))
                            {
                                ActivityLog.LogWarning(nameof(SilkyUIColorAdornmentTagger), "颜色值已变化，取消本次颜色替换。");
                                return;
                            }
                            if (!edit.Replace(currentSpan.Span, newColorString))
                            {
                                ActivityLog.LogWarning(nameof(SilkyUIColorAdornmentTagger), "颜色替换失败，目标范围可能为只读。");
                                return;
                            }
                            edit.Apply();
                            if (edit.Canceled)
                                ActivityLog.LogWarning(nameof(SilkyUIColorAdornmentTagger), "颜色替换被编辑器取消。");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException ||
                                       ex is System.Runtime.InteropServices.ExternalException)
            {
                ActivityLog.LogError(nameof(SilkyUIColorAdornmentTagger), $"颜色选择或回写失败：{ex}");
            }
        });
    }
}
