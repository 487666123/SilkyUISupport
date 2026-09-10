using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

namespace SilkyUISupport;

/// <summary>
/// SilkyUI 元数据查询服务（公共服务，供所有功能使用）
/// </summary>
[Export(typeof(SilkyUIMetadataService))]
internal class SilkyUIMetadataService : IPartImportsSatisfiedNotification
{
    public static string XmlElementMappingAttributeGlobalName { get; } = "SilkyUIFramework.Attributes.XmlElementMappingAttribute";

    public static string RefreshFaultEventName { get; } = "SilkyUISupport/SilkyUIMetadataService.Refresh";

    [Import]
    public AttributeClassScanner ClassScanner { get; set; } = null;

    [Import]
    public VisualStudioWorkspace Workspace { get; set; }

    /// <summary>元数据就绪后触发，供消费者刷新自身状态。</summary>
    public event Action Refreshed;

    private bool _isDirty = true;
    private int _isRefreshing;

    private List<XmlMappingClass> _cachedClasses = [];
    private List<SilkyUIElementGroupClass> _cachedUIClasses = [];
    private ImmutableList<SilkyUIProperty> _cachedStyleProperties = [];
    private ImmutableList<SilkyUITargetClass> _cachedTargetClasses = [];
    private ConcurrentDictionary<string, ImmutableList<SilkyUIProperty>> _targetPropertyCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取所有 SilkyUI 类（带缓存）
    /// </summary>
    public ImmutableList<XmlMappingClass> GetAllClasses() => [.. _cachedClasses];

    /// <summary>
    /// 获取继承自 UIElementGroup 的类（Body Class 补全用）。
    /// </summary>
    public ImmutableList<SilkyUIElementGroupClass> GetAllGroupClasses() => [.. _cachedUIClasses];

    /// <summary>获取所有映射元素公开属性的合并集合（按首次出现的属性名去重）。</summary>
    public ImmutableList<SilkyUIProperty> GetAllStyleProperties() => _cachedStyleProperties;

    /// <summary>获取所有公开类，供 sui:Target 补全使用。</summary>
    public ImmutableList<SilkyUITargetClass> GetAllTargetClasses() => _cachedTargetClasses;

    /// <summary>获取指定公开类及其基类的公开属性。</summary>
    public ImmutableList<SilkyUIProperty> GetTargetProperties(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return [];

        var target = _cachedTargetClasses.FirstOrDefault(item => item.FullName == fullName);
        if (target == null) return [];

        return _targetPropertyCache.GetOrAdd(target.FullName, _ =>
            [.. ClassScanner.GetPublicReadableProperties(target.Class)]);
    }

    /// <summary>
    /// 根据类名获取 SilkyUI 类
    /// </summary>
    public XmlMappingClass GetClassByName(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;

        return GetAllClasses().FirstOrDefault(c => c.Alias == className);
    }

    /// <summary>
    /// 根据类名和属性名获取属性
    /// </summary>
    public SilkyUIProperty GetPropertyByName(string className, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(propertyName)) return null;

        return GetClassByName(className)?.Properties.FirstOrDefault(p => p.Property.Name == propertyName);
    }

    private static ImmutableList<SilkyUIProperty> BuildStyleProperties(IEnumerable<XmlMappingClass> classes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var properties = new List<SilkyUIProperty>();

        foreach (var xmlClass in classes)
            foreach (var property in xmlClass.Properties)
                if (seen.Add(property.Property.Name))
                    properties.Add(property);

        return [.. properties];
    }

    #region 刷新任务

    void IPartImportsSatisfiedNotification.OnImportsSatisfied()
    {
        Workspace.WorkspaceChanged += OnWorkspaceChanged;
        RefreshLoopAsync().FileAndForget(RefreshFaultEventName);
    }

    private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
    {
        _isDirty = true;
        RefreshLoopAsync().FileAndForget(RefreshFaultEventName);
    }

    private async Task RefreshLoopAsync()
    {
        // 确保只有一个刷新任务
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1) return;

        try
        {
            _isDirty = false;

            var classes = await Task.Run(() => ClassScanner.GetClassesWithAttributeAsync(Workspace, XmlElementMappingAttributeGlobalName));
            Interlocked.Exchange(ref _cachedClasses, classes);
            Interlocked.Exchange(ref _cachedStyleProperties, BuildStyleProperties(classes));

            var targetClasses = await Task.Run(() => ClassScanner.GetAllPublicClassesAsync(Workspace));
            Interlocked.Exchange(ref _cachedTargetClasses, [.. targetClasses]);
            Interlocked.Exchange(ref _targetPropertyCache, new ConcurrentDictionary<string, ImmutableList<SilkyUIProperty>>(StringComparer.Ordinal));

            var groupClasses = await Task.Run(() => ClassScanner.GetUIElementGroupClassesAsync(Workspace));
            Interlocked.Exchange(ref _cachedUIClasses, groupClasses);

            Refreshed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
            if (_isDirty) RefreshLoopAsync().FileAndForget(RefreshFaultEventName);
        }
    }

    #endregion
}
