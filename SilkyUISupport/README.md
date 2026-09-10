# SilkyUI Support

`SilkyUI Support` 是面向 Visual Studio 2022 的 VSIX 扩展，为 SilkyUI 的 `.sui.xml` 文件提供基于当前解决方案 C# 符号的编辑辅助功能。

## 功能

### 智能补全

- 补全 XML 标签：`Body`、已映射的 SilkyUI 元素和 `sui:Style`。
- 补全普通属性、`bind:*` 绑定属性、SilkyUI 特殊属性和枚举属性值。
- 在 `<sui:Style>` 节点中，除 `sui:Name` 外的无前缀属性来自所有映射元素公开属性合并去重后的集合；同名属性保留首次出现项的类型信息。
- 支持使用 `sui:Target` 指定属性补全来源类；补全列表显示短类名，选中后写入当前解决方案 C# 项目中的公开类全限定名，右侧说明保留全限定名。
- 从当前解决方案的 C# 项目中读取带有 `XmlElementMappingAttribute` 的公开类，以及继承自 `UIElementGroup` 的公开类，动态生成补全内容。
- 为 `Body sui:Class` 补全可用的 `UIElementGroup` 类，并为元素补全对应的公开属性。
- 支持使用任意前缀，只要前缀绑定到 SilkyUI 或 Binding 的正确命名空间 URI。

### 成员元素 `M.Xxx`

`M.Xxx` 用于展开父元素的对象属性，并在该对象上设置子属性。例如：

```xml
<Body xmlns:sui="https://github.com/487666123/SilkyUIFramework"
      sui:Class="MyMod.MyPanel">
    <ScrollView>
        <M.Mask Border="2" BorderRadius="4" />
    </ScrollView>
</Body>
```

- `M.` 后的成员来自父元素的公开实例属性。
- 只有类型为 class 或 interface 的对象属性可以作为 `M.` 成员；`bool`、`int`、`float`、`double` 等标量属性不会出现在成员补全中。
- 成员元素的属性来自目标对象及其基类的公开可读写属性，枚举属性仍提供枚举值补全。
- 只有目标类型继承自 `SilkyUIFramework.Elements.UIView` 时，成员元素才提供 `bind:*` 补全。
- 成员元素支持 `sui:Style`。

### 编辑器辅助

- 悬停在已解析的元素或属性上时，显示对应的 C# 类型、声明类型、属性类型和枚举值等信息。
- 对已解析的元素名和属性名提供定义导航，包括 `M.Xxx` 成员元素和成员属性。
- 为已知和未知元素、普通属性、特殊属性及未知属性使用不同的编辑器分类颜色。
- 检查未知元素、重复属性和无效的 `Body sui:Class`；对可解析的 `Body` 或普通元素属性检查未知属性和非法枚举值，并显示错误标记。

### XML 模板

在 C# 编辑器中右键当前类，可以执行“创建 SilkyUI XML 初始模板”：

- 在类所在目录创建同名的 `.sui.xml` 文件。
- 文件已经存在时不覆盖原内容，而是直接打开现有文件。
- 模板以 `Body` 为根元素，并写入当前类的 `sui:Class`。

## XML 基础用法

UI 文件必须使用 `.sui.xml` 后缀。根元素通常声明以下命名空间：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Body xmlns:sui="https://github.com/487666123/SilkyUIFramework"
      xmlns:bind="https://github.com/487666123/SilkyUIFramework/Binding"
      sui:Class="MyMod.MyPanel">
</Body>
```

- `sui:Class` 写在根元素上，用于指定继承自 `UIElementGroup` 的 C# 类；建议填写全限定名称。
- `sui:Name` 写在普通元素上，用于生成对应的 C# 元素属性。
- `sui:Style` 引用一个或多个样式名称，多个名称用空格分隔。
- `sui:Style` 元素使用 `sui:Name` 定义样式名称；样式属性直接写在该元素上。
- `sui:Target` 写在 `sui:Style` 元素上，填写 C# 公开类的全限定名称，用于限定该样式节点的属性补全来源；补全列表显示短类名，但插入值和右侧说明使用全限定名。
- `bind:Text="Title"` 表示把数据源的 `Title` 绑定到元素的 `Text` 属性。

## 当前边界

- VSIX 的 `M.Xxx` 补全、颜色和导航当前只解析直接挂在 `Body` 或普通元素下的成员元素。
- 不支持嵌套成员路径，例如 `M.M.Xxx`。
- `M.Xxx` 用于设置对象的子属性，不支持通过元素文本为标量属性赋值，例如 `<M.Border>2</M.Border>`。
- `sui:Target` 当前用于 VSIX 属性补全来源选择，不改变 `SilkyUIAnalyzer` 对样式的生成逻辑。

## 安装与构建

当前 VSIX 清单声明的环境要求：

- Visual Studio 2022 17.14 或更高版本。
- 64 位 Visual Studio；清单中的产品架构为 `amd64`，安装目标为 `Microsoft.VisualStudio.Community`。
- 项目目标框架为 .NET Framework 4.7.2。

在 `ModSources` 目录执行：

```powershell
dotnet build SilkyUISupport\SilkyUISupport\SilkyUISupport.csproj --no-restore
```

构建完成后，安装以下目录中的 VSIX 文件：

- `SilkyUISupport\SilkyUISupport\bin\Debug\net472\SilkyUISupport.vsix`
- 或对应配置下的 `bin\Release\net472\SilkyUISupport.vsix`
