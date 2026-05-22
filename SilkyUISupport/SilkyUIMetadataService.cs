using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;

namespace SilkyUISupport;

/// <summary>
/// SilkyUI 元数据查询服务（公共服务，供所有功能使用）
/// </summary>
[Export(typeof(SilkyUIMetadataService))]
internal class SilkyUIMetadataService : IPartImportsSatisfiedNotification
{
    private const string TargetAttributeName = "SilkyUIFramework.Attributes.XmlElementMappingAttribute";

    [Import]
    public AttributeClassScanner ClassScanner { get; set; } = null!;

    [Import]
    public VisualStudioWorkspace Workspace { get; set; }

    private bool _isDirty = true;
    private int _isRefreshing;

    private List<SilkyUIClass> _cachedClasses = [];
    private List<SilkyUIElementGroupClass> _cachedUIClasses = [];

    public void OnImportsSatisfied()
    {
        Workspace.WorkspaceChanged += Workspace_WorkspaceChanged;
        _ = RefreshLoopAsync();
    }

    private void Workspace_WorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
    {
        _isDirty = true;
        _ = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1) return;

        try
        {
            _isDirty = false;
            Interlocked.Exchange(ref _cachedClasses,
                await Task.Run(() => ClassScanner.GetClassesWithAttribute(Workspace, TargetAttributeName)));
            Interlocked.Exchange(ref _cachedUIClasses,
                await Task.Run(() => ClassScanner.GetUIElementGroupClasses(Workspace)));
            Refreshed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
            if (_isDirty) _ = RefreshLoopAsync();
        }
    }

    /// <summary>
    /// 获取所有 SilkyUI 类（带缓存）
    /// </summary>
    public List<SilkyUIClass> GetAllClasses() => _cachedClasses;

    /// <summary>元数据就绪后触发，供消费者刷新自身状态。</summary>
    public event Action? Refreshed;

    /// <summary>
    /// 获取继承自 UIElementGroup 的类（Body Class 补全用）。
    /// </summary>
    public List<SilkyUIElementGroupClass> GetUIClasses() => _cachedUIClasses;

    /// <summary>
    /// 根据类名获取 SilkyUI 类
    /// </summary>
    public SilkyUIClass GetClassByName(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;

        return GetAllClasses().FirstOrDefault(c => c.Name == className);
    }

    /// <summary>
    /// 根据类名和属性名获取属性
    /// </summary>
    public SilkyUIProperty GetPropertyByName(string className, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        return GetClassByName(className)?.Properties.FirstOrDefault(p => p.Name == propertyName);
    }
}
