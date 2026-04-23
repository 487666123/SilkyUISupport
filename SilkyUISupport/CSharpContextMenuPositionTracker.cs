using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

/// <summary>
/// 在文本视图上保存最近一次右键点击的位置，
/// 供上下文菜单命令执行时还原真正的触发点。
/// </summary>
internal static class CSharpContextMenuPositionState
{
    private sealed class PropertyKey;

    public static void Set(IWpfTextView textView, SnapshotPoint point)
    {
        // 使用 TrackingPoint，避免在用户继续编辑后位置信息立即失效。
        var trackingPoint = point.Snapshot.CreateTrackingPoint(point.Position, PointTrackingMode.Positive);
        textView.Properties[typeof(PropertyKey)] = trackingPoint;
    }

    public static bool TryGet(IWpfTextView textView, ITextSnapshot snapshot, out SnapshotPoint point)
    {
        if (textView.Properties.TryGetProperty(typeof(PropertyKey), out ITrackingPoint trackingPoint))
        {
            point = trackingPoint.GetPoint(snapshot);
            return true;
        }

        point = default;
        return false;
    }
}

/// <summary>
/// 为 C# 编辑器注册鼠标处理器，用于捕获右键菜单弹出前的点击位置。
/// </summary>
[Export(typeof(IMouseProcessorProvider))]
[Name("SilkyUI CSharp context menu position tracker")]
[ContentType("CSharp")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class CSharpContextMenuPositionTrackerProvider : IMouseProcessorProvider
{
    public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
        => wpfTextView.Properties.GetOrCreateSingletonProperty(() => new CSharpContextMenuPositionTracker(wpfTextView));
}

/// <summary>
/// 右键时光标不一定会移动到鼠标所在位置，
/// 因此需要在菜单弹出前主动记录点击点。
/// </summary>
internal sealed class CSharpContextMenuPositionTracker(IWpfTextView textView) : MouseProcessorBase
{
    private readonly IWpfTextView _textView = textView;

    public override void PreprocessMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (TryGetBufferPosition(e.GetPosition(_textView.VisualElement), out var point))
        {
            CSharpContextMenuPositionState.Set(_textView, point);
        }
    }

    private bool TryGetBufferPosition(Point mousePosition, out SnapshotPoint point)
    {
        point = default;

        try
        {
            // 先定位到鼠标所在可视行，再按 X 坐标换算回文本缓冲区位置。
            var textViewLine = _textView.TextViewLines.GetTextViewLineContainingYCoordinate(mousePosition.Y);
            var bufferPosition = textViewLine.GetBufferPositionFromXCoordinate(mousePosition.X);
            if (!bufferPosition.HasValue)
                return false;

            point = bufferPosition.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
