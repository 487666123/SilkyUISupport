using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace SilkyUISupport;

/// <summary>
/// SilkyUI 元数据查询服务（公共服务，供所有功能使用）
/// </summary>
[Export(typeof(SilkyUIMetadataService))]
internal class SilkyUIMetadataService
{
    [Import]
    public AttributeClassScanner ClassScanner { get; set; } = null!;

    private const string TargetAttributeName = "SilkyUIFramework.Attributes.XmlElementMappingAttribute";

    private List<SilkyUIClass> _cachedClasses;

    /// <summary>
    /// 获取所有 SilkyUI 类（带缓存）
    /// </summary>
    public List<SilkyUIClass> GetAllClasses()
    {
        return _cachedClasses ??= ClassScanner.GetClassesWithAttribute(TargetAttributeName);
    }

    /// <summary>
    /// 根据类名获取 SilkyUI 类
    /// </summary>
    public SilkyUIClass GetClassByName(string className)
    {
        if (string.IsNullOrEmpty(className))
            return null;

        return GetAllClasses().FirstOrDefault(c => c.Name == className);
    }

    /// <summary>
    /// 根据类名和属性名获取属性
    /// </summary>
    public SilkyUIProperty GetPropertyByName(string className, string propertyName)
    {
        if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(propertyName))
            return null;

        var @class = GetClassByName(className);
        return @class?.Properties.FirstOrDefault(p => p.Name == propertyName);
    }

    /// <summary>
    /// 清除缓存（当项目文件变化时调用）
    /// </summary>
    public void ClearCache()
    {
        _cachedClasses = null;
    }
}
