using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

/// <summary>
/// 在文本视图上保存当前鼠标上下文菜单的触发位置，
/// 后续编辑器输入或命令执行结束该位置的有效期。
/// </summary>
internal static class CSharpContextMenuPositionState
{
    private sealed class PropertyKey;

    public static void Set(IWpfTextView textView, SnapshotPoint point)
    {
        // 编辑后不再沿用旧菜单的触发位置。
        textView.Properties[typeof(PropertyKey)] = point;
    }

    public static bool TryGet(IWpfTextView textView, ITextSnapshot snapshot, out SnapshotPoint point)
    {
        if (!textView.IsClosed &&
            textView.Properties.TryGetProperty(typeof(PropertyKey), out SnapshotPoint savedPoint) &&
            savedPoint.Snapshot == snapshot)
        {
            // BeforeQueryStatus 可以反复查询；仅在执行命令或收到新输入时清除。
            point = savedPoint;
            return true;
        }

        Clear(textView);
        point = default;
        return false;
    }

    public static void Clear(IWpfTextView textView)
        => textView.Properties.RemoveProperty(typeof(PropertyKey));

    public static void ClearForKeyboardInvocation(IWpfTextView textView)
    {
        // VS 可能在 WPF 收到按键前处理菜单快捷键；查询菜单状态时也检查。
        if (Keyboard.IsKeyDown(Key.Apps) ||
            (Keyboard.IsKeyDown(Key.F10) && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            Clear(textView);
        }
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
internal sealed class CSharpContextMenuPositionTracker : MouseProcessorBase
{
    private readonly IWpfTextView _textView;
    private bool _processingRightClick;

    public CSharpContextMenuPositionTracker(IWpfTextView textView)
    {
        _textView = textView;
        _textView.VisualElement.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        _textView.Caret.PositionChanged += OnCaretOrSelectionChanged;
        _textView.Selection.SelectionChanged += OnCaretOrSelectionChanged;
        _textView.TextBuffer.Changed += OnTextChanged;
        _textView.Closed += OnClosed;
    }

    public override void PreprocessMouseRightButtonDown(MouseButtonEventArgs e)
    {
        CSharpContextMenuPositionState.Clear(_textView);
        _processingRightClick = true;
        if (!_textView.IsClosed && _textView.VisualElement != null &&
            TryGetBufferPosition(e.GetPosition(_textView.VisualElement), out var point))
        {
            CSharpContextMenuPositionState.Set(_textView, point);
        }
    }

    public override void PostprocessMouseRightButtonDown(MouseButtonEventArgs e)
        => _processingRightClick = false;

    public override void PreprocessMouseRightButtonUp(MouseButtonEventArgs e)
        => _processingRightClick = true;

    public override void PostprocessMouseRightButtonUp(MouseButtonEventArgs e)
        => _processingRightClick = false;

    public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e)
        => ClearOrigin();

    public override void PreprocessMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
            ClearOrigin();
    }

    public override void PreprocessMouseWheel(MouseWheelEventArgs e)
        => ClearOrigin();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        => ClearOrigin(); // 包括 Shift+F10、Apps 键及其他编辑器键盘命令。

    private void OnCaretOrSelectionChanged(object sender, EventArgs e)
    {
        // 默认右键处理器本身可能移动光标/选区，仍需保留这次点击位置。
        if (!_processingRightClick)
            CSharpContextMenuPositionState.Clear(_textView);
    }

    private void OnTextChanged(object sender, TextContentChangedEventArgs e)
        => ClearOrigin();

    private void ClearOrigin()
    {
        _processingRightClick = false;
        CSharpContextMenuPositionState.Clear(_textView);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _textView.VisualElement.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        _textView.Caret.PositionChanged -= OnCaretOrSelectionChanged;
        _textView.Selection.SelectionChanged -= OnCaretOrSelectionChanged;
        _textView.TextBuffer.Changed -= OnTextChanged;
        _textView.Closed -= OnClosed;
        ClearOrigin();
    }

    private bool TryGetBufferPosition(Point mousePosition, out SnapshotPoint point)
    {
        point = default;

        try
        {
            // WPF 鼠标位置相对视口；行及字符命中测试使用文本渲染坐标。
            var textViewLine = _textView.TextViewLines?.GetTextViewLineContainingYCoordinate(
                mousePosition.Y + _textView.ViewportTop);
            if (textViewLine == null)
                return false;

            var bufferPosition = textViewLine.GetBufferPositionFromXCoordinate(mousePosition.X + _textView.ViewportLeft);
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
