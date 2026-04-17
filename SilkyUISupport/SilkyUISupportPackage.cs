using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace SilkyUISupport;

/// <summary>
/// 这是实现此程序集公开的包的类。
/// </summary>
/// <remarks>
/// <para>
/// 一个类要被视为有效的Visual Studio包，最低要求是实现IVsPackage接口并向shell注册自己。
/// 这个包使用托管包框架(MPF)中定义的辅助类来实现：它派生自提供IVsPackage接口实现的Package类，
/// 并使用框架中定义的注册属性向shell注册自身及其组件。这些属性告诉pkgdef创建工具要将什么数据放入.pkgdef文件。
/// </para>
/// <para>
/// 要加载到VS中，包必须在.vsixmanifest文件中通过&lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt;引用。
/// </para>
/// </remarks>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(SilkyUIExtensionPackage.PackageGuidString)]
public sealed class SilkyUIExtensionPackage : AsyncPackage
{
    /// <summary>
    /// SilkyUIExtensionPackage GUID字符串。
    /// </summary>
    public const string PackageGuidString = "03dc73cd-a6cd-4b46-8e3a-86fda434177c";

    #region Package Members

    /// <summary>
    /// 包的初始化方法；此方法在包被站点后立即调用，因此你可以将所有依赖于VisualStudio提供的服务的初始化代码放在这里。
    /// </summary>
    /// <param name="cancellationToken">用于监视初始化取消的取消令牌，当VS关闭时可能会发生取消。</param>
    /// <param name="progress">进度更新提供程序。</param>
    /// <returns>表示包初始化异步工作的任务，如果没有工作则返回已完成的任务。不要从此方法返回null。</returns>
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // 异步初始化时，此时当前线程可能是后台线程。
        // 切换到UI线程后，再执行任何需要UI线程的初始化操作。
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    #endregion
}
