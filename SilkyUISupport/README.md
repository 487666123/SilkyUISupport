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
- 支持使用任意前缀，只要前缀绑定到 SilkyUI、Binding 或 Properties 的正确命名空间 URI。

### CLR 命名空间导入

可以直接使用未声明 `XmlElementMappingAttribute` 的 C# 类，同时保留原有无命名空间标签的别名映射：

```xml
<Body xmlns:sui="https://github.com/487666123/SilkyUIFramework"
      xmlns:local="clr-namespace:MyMod.Elements"
      xmlns:ext="clr-namespace:OtherMod.Controls"
      sui:Class="MyMod.MyPanel">
    <local:StatusPanel />
    <ext:ProgressIndicator />
</Body>
```

- 在 `xmlns` 值中补全 `clr-namespace:` 和当前项目及可见引用中的命名空间；输入 `<local:` 后补全该命名空间中可创建的类型。声明仅接受 `clr-namespace:Namespace`，不再接受或补全 `;assembly=...`。
- CLR 标签按 C# 的 `global::Namespace.Type` 解析，范围包含 XML 所属项目及通过 `global` 引用别名可见的依赖；不会自动添加引用，仅通过其他 `extern alias` 暴露的类型不参与补全和解析。源码类型优先于引用中的同名类型，无法消除的歧义会显示错误，歧义类型不会作为补全候选。
- XML 项目归属优先采用 Documents/AdditionalDocuments 路径，否则采用最长项目目录匹配；链接文件属于多个项目或项目目录歧义时不猜测目标项目。
- 候选为可访问、非抽象、非静态、非泛型的顶层类，且有可访问的无参构造函数。不要求映射特性或容器接口。包含 required 成员时，无参构造函数须声明 SetsRequiredMembers。
- 类型名称和命名空间区分大小写；声明不接受空白或程序集参数。CLR 名称不写 C# 的 `@` 转义，命名空间为空表示全局命名空间。
- 支持默认命名空间及嵌套声明覆盖。默认 CLR 命名空间内要使用旧别名，可写 `xmlns=""`；属性节点按 Properties 命名空间 URI 识别。
- 已解析的 CLR 元素复用属性/枚举补全、悬停、分类和可用源码导航。根元素上的 `sui:Class` 仍指定被初始化的现有根对象，不会根据根标签创建新对象。
- 声明格式错误、类型不可用或有歧义、类型无法创建时显示错误标记；显式 CLR 标签不会回退到同名别名。
- 最外层开始标签支持 `sui:Class` 属性名及类名值补全，不要求先写完整属性值或闭合开始标签；默认 CLR 命名空间和自定义根标签也适用，子标签不提供根类补全。
- 已绑定 `sui:Class` 时，与 Analyzer 使用相同的根类源码声明上下文。文件局部类型不受支持；同文件的 `file` 类型遮蔽其他同名类型时会保守报错，不回退选择另一类型。尚未填写根类时只提供项目级预览候选。
- 本功能不增加字典、基础类型直接值、泛型标签或带参构造语法。生成代码需要同步使用支持 CLR 导入的 SilkyUIAnalyzer。

### 成员元素 `prop:Xxx`

`prop:Xxx` 用于展开父元素的对象属性，并在该对象上设置子属性。例如：

```xml
<Body xmlns:sui="https://github.com/487666123/SilkyUIFramework"
      xmlns:prop="https://github.com/487666123/SilkyUIFramework/Properties"
      sui:Class="MyMod.MyPanel">
    <ScrollView>
        <prop:Mask Border="2" BorderRadius="4" />
    </ScrollView>
</Body>
```

- 属性节点的 localName 来自父元素的公开实例属性；推荐 `prop` 前缀，但按 `https://github.com/487666123/SilkyUIFramework/Properties` URI 识别，支持别名前缀、默认命名空间和嵌套覆盖。
- 在空标签 `<` 处提供作用域内已声明的属性节点候选；输入 `<p:` 后仅使用当前 `p` 声明，前缀没有固定名称。默认 Properties 命名空间提供无前缀属性节点，不混入无命名空间控件别名。
- `xmlns` 值补全包含 Properties URI；默认命名空间不影响节点上无前缀属性的直接赋值语义。
- 只有类型为 class 或 interface 的对象属性可以作为 `prop:` 成员；`bool`、`int`、`float`、`double` 等标量属性不会出现在成员补全中。
- 支持逐层嵌套的成员元素；每层依据直接父元素对应的对象类型解析。
- 展开对象属性只要求公开 getter，不要求 setter，因此只有 getter 或 setter 不公开的对象属性也能继续展开。
- 查找包含基类及继承接口的属性；直接赋值和绑定补全仍要求公开 setter，枚举属性仍提供枚举值补全。
- 只有目标类型继承自 `SilkyUIFramework.Elements.UIView` 时，成员元素才提供 `bind:*` 补全。
- 成员元素支持 `sui:Style`。

例如，假设映射元素 `View` 有 `Appearance` 对象属性，该对象又有 `Border` 对象属性，可以写成：

```xml
<View xmlns:prop="https://github.com/487666123/SilkyUIFramework/Properties">
    <prop:Appearance>
        <prop:Border Width="2" Color="#0099ff" />
    </prop:Appearance>
</View>
```

这对应设置 `view.Appearance.Border` 的属性。各层对象需要事先初始化；Support 提供嵌套成员和属性的补全、诊断、悬停、导航及颜色预览。

### 编辑器辅助

- 悬停在已解析的元素或属性上时，显示对应的 C# 类型、声明类型、属性类型和枚举值等信息；`Body` 标签及其 `sui:Class` 属性值显示对应的 C# 类名。
- 通过 Ctrl+左键对已解析的元素名和属性名进行定义导航，包括 `prop:Xxx` 成员元素和成员属性。
- Ctrl+左键点击 `<Body>`、与其配对的 `</Body>` 标签名，或 `sui:Class="..."` 中的类名值，可以跳转到对应 `UIElementGroup` 派生类的定义；`Class` 属性按实际绑定的命名空间 URI 识别，不限定前缀必须是 `sui`。
- 为已知和未知元素、普通属性、特殊属性及未知属性使用不同的编辑器分类颜色。
- 检查未知元素、重复属性和无效的 `Body sui:Class`；对可解析的 `Body` 或普通元素属性检查未知属性、非法枚举值和只读属性赋值，并显示错误标记。

### 编辑性能

- 分类和诊断分别缓存一份文档快照与元数据快照对应的分析结果；只有两个快照都相同时才复用，减少同一快照被多次查询时的全文分析。
- 在当前语义模型内按标签对象缓存元素解析结果，避免同一标签的多个属性反复解析所属元素；完整标签与光标处的未完成标签投影分别处理。
- 补全列表使用批量更新合并中间集合通知，保留原有匹配算法、候选顺序和最佳匹配选择逻辑。
- 为映射类别名及 `UIElementGroup` 类的全名、短名建立查询索引；同名时保留第一个结果，类名查询仍优先匹配全名，再匹配短名，补全展示继续使用原列表顺序。
- 扫描 XML 映射类时，遇到首个有效映射别名后才收集属性，同一个类的后续别名复用该属性集合。

这些优化不改变 XML 解析及原有属性语义规则。分类和诊断仍同步执行，并保留全文失效通知；新快照的首次查询仍需分析，未引入后台防抖或增量 XML 解析。

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
      xmlns:prop="https://github.com/487666123/SilkyUIFramework/Properties"
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

- 支持 XML 元素逐层嵌套，不支持在单个标签中写点分成员路径，例如 `<prop:Appearance.Border />`。
- 成员展开仅支持 class 或 interface 对象属性，不支持结构体属性的逐层修改与写回，也不会自动创建为 null 的中间对象。
- `prop:Xxx` 用于设置对象的子属性，不支持通过元素文本为标量属性赋值，例如 `<prop:Border>2</prop:Border>`。
- `sui:Target` 当前用于 VSIX 属性补全来源选择，不改变 `SilkyUIAnalyzer` 对样式的生成逻辑。
- 定义导航需要能解析出对应符号及其源码位置。`Body` 类导航要求 `sui:Class` 的值完整且能匹配已发现的 `UIElementGroup` 派生类；未知类、未完成的属性值或没有源码位置的类不提供跳转。

## 文档

- [架构说明](../docs/architecture.md)
- [XML 解析与人工验证](../docs/xml-parsing.md)

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
