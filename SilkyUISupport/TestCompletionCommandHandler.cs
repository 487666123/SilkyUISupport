using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

[Name("token completion handler")]
[Export(typeof(IVsTextViewCreationListener))]
[ContentType("SilkyUI XML")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal class TestCompletionHandlerProvider : IVsTextViewCreationListener
{
    [Import]
    internal IVsEditorAdaptersFactoryService AdapterService = null; // 编辑器适配器服务，用于新旧编辑器交互

    [Import]
    internal ICompletionBroker CompletionBroker { get; set; } // 补全管理器，负责创建和管理补全会话

    [Import]
    internal SVsServiceProvider ServiceProvider { get; set; } // 服务提供者，用于获取VS的各种系统服务

    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        // 把旧版的IVsTextView转换成新版的WPF编辑器接口
        if (AdapterService.GetWpfTextView(textViewAdapter) is not ITextView textView) return;

        // 创建补全命令处理程序的工厂方法
        // 把命令处理程序注册到编辑器的属性中，每个编辑器只创建一个实例
        textView.Properties.GetOrCreateSingletonProperty(() => new TestCompletionCommandHandler(textViewAdapter, textView, this));
    }
}

/*
 * 【初学者注释】
 * 这个类是补全功能的"命令处理核心"，实现了IOleCommandTarget接口
 * IOleCommandTarget是Visual Studio中处理键盘/菜单命令的标准接口
 * 所有的键盘输入都会先经过这个类处理，我们可以在这里拦截需要的按键来控制补全功能
 */
internal class TestCompletionCommandHandler : IOleCommandTarget
{
    private readonly TestCompletionHandlerProvider m_provider; // 上层的提供者实例，用来获取各种服务
    private readonly ITextView m_textView; // 当前编辑器实例

    private readonly IOleCommandTarget m_nextCommandHandler; // 命令链中的下一个处理程序，我们处理不了的命令要传给它

    private ICompletionSession m_session; // 当前的补全会话，相当于补全弹窗的控制器

    internal TestCompletionCommandHandler(IVsTextView textViewAdapter, ITextView textView, TestCompletionHandlerProvider provider)
    {
        m_textView = textView;
        m_provider = provider;

        // 将命令添加到命令链中
        // 命令链是Visual Studio的命令处理机制：命令会依次经过链上的每个处理程序，直到被处理
        textViewAdapter.AddCommandFilter(this, out m_nextCommandHandler);
    }

    /*
     * 查询命令状态的方法，是IOleCommandTarget接口的实现
     * 这个方法用来告诉VS我们是否支持某个命令，这里我们直接把查询传递给下一个处理程序
     */
    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return m_nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }

    /*
     * 执行命令的方法，是IOleCommandTarget接口的核心实现
     * 所有的键盘输入、菜单命令都会调用这个方法
     * 我们在这里拦截需要的按键，来控制补全功能的弹出、过滤、提交和关闭
     */
    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // 如果VS正处于自动化操作中(比如宏运行、代码生成)，我们不处理命令，直接传递给下一个处理程序
        if (VsShellUtilities.IsInAutomationFunction(m_provider.ServiceProvider))
        {
            return m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        // 复制一份命令ID，这样在转发一些命令后我们还能查看它
        uint commandID = nCmdID;
        char typedChar = char.MinValue; // 用户输入的字符

        // 检查是否是普通字符输入命令(就是用户按了键盘上的字母/数字/符号键)
        if (pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
        {
            // 从原生内存中读取输入的字符，pvaIn是指向输入参数的指针
            typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
        }

        // 处理补全提交操作
        // 提交字符：回车、Tab、空格、标点符号
        // 这些字符都应该触发补全项的提交
        if (nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
            || char.IsWhiteSpace(typedChar) || char.IsPunctuation(typedChar))
        {
            // 检查当前有没有打开的补全弹窗
            if (m_session is { IsDismissed: false })
            {
                // 如果用户已经选中了某个补全项
                if (m_session.SelectedCompletionSet.SelectionStatus.IsSelected)
                {
                    m_session.Commit(); return VSConstants.S_OK;
                }
                else m_session.Dismiss();
            }
        }

        // 先把命令传递给下一个处理程序，让字符先输入到编辑器里
        // 比如用户输入了字母'a'，先让'a'出现在编辑器中，然后我们再弹出补全
        var retVal = m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        var handled = false;

        // 处理补全弹出和过滤
        // 触发补全的字符：字母、数字、等号（=）、双引号（"）
        if (!typedChar.Equals(char.MinValue) &&
            (char.IsLetterOrDigit(typedChar) || typedChar == '=' || typedChar == '"'))
        {
            if (m_session is not { IsDismissed: false })
                TriggerCompletion();

            // 只有会话存在且未关闭时才调用 Filter
            if (m_session is { IsDismissed: false })
                m_session.Filter();

            handled = true;
        }
        else if (commandID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
            || commandID == (uint)VSConstants.VSStd2KCmdID.DELETE)
        {
            if (m_session is { IsDismissed: false })
                m_session.Filter();
            handled = true;
        }

        return handled ? VSConstants.S_OK : retVal;
    }

    /*
     * 触发补全功能的方法，用来创建并打开补全弹窗
     * 当用户输入字母/数字时会调用这个方法
     */
    private bool TriggerCompletion()
    {
        // 第一步：先检查当前编辑器有没有已经打开的补全会话，有就直接复用，不创建新的
        var existingSessions = m_provider.CompletionBroker.GetSessions(m_textView);
        if (existingSessions.Count > 0)
        {
            m_session = existingSessions[0]; return true;
        }

        // 第二步：如果没有已存在的会话，才创建新的
        var caretPoint = m_textView.Caret.Position.Point.GetPoint(
            textBuffer => (!textBuffer.ContentType.IsOfType("projection")), PositionAffinity.Predecessor);

        if (!caretPoint.HasValue) return false;

        m_session = m_provider.CompletionBroker.CreateCompletionSession(
            m_textView,
            caretPoint.Value.Snapshot.CreateTrackingPoint(caretPoint.Value.Position, PointTrackingMode.Positive),
            true);

        m_session.Dismissed += this.OnSessionDismissed;
        m_session.Start();
        return true;
    }

    private void OnSessionDismissed(object sender, EventArgs e)
    {
        m_session.Dismissed -= OnSessionDismissed;
        m_session = null;
    }
}