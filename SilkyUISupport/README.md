# SilkyUI Support

## 简介

`SilkyUI Support` 是一个面向 Visual Studio 2022 的 VSIX 扩展，用于为 `SilkyUI` 的 `.sui.xml` 标记文件提供上下文感知补全。

扩展会扫描当前解决方案中的 C# 项目，读取带有 `SilkyUIFramework.Attributes.XmlElementMappingAttribute` 特性的公开类，并根据这些类和属性动态生成补全项。因此，补全内容会尽量跟随项目中的实际 SilkyUI 类型定义，而不是依赖手写字典。

## 功能

- 标签名补全：在 `.sui.xml` 文件中输入 `<` 后，提示可映射的 SilkyUI 元素标签。
- 属性名补全：在标签内部根据当前标签提示对应属性。
- 枚举属性值补全：在属性值的双引号内，为枚举类型属性提示可用枚举值。
- 继承属性补全：会读取类本身及其基类中的公开可读写属性。
- 仅作用于 `.sui.xml` 文件，不影响普通 `.xml` 文件。

## 安装

1. 使用 Visual Studio 2022 打开 `SilkyUISupport.slnx`。
2. 构建解决方案，生成 `.vsix` 扩展包。
3. 双击生成出的 `.vsix` 文件并安装到 Visual Studio 2022。
4. 重启 Visual Studio。

运行环境要求：

- Visual Studio 2022 17.14 或更高版本
- 64 位 Visual Studio（`amd64`）
- 已安装 Visual Studio Core Editor
- 解决方案中存在可被 Roslyn 编译分析的 C# 项目

## 使用

1. 打开包含 SilkyUI 相关 C# 项目的解决方案。
2. 打开或创建后缀为 `.sui.xml` 的文件。
3. 在 XML 标签、属性或属性值位置输入内容触发补全。

示例：

```xml
<!-- 标签名补全 -->
<But

<!-- 属性名补全 -->
<Button Vis

<!-- 枚举属性值补全 -->
<Button Visibility="Vis"
```

当前属性值补全只支持双引号场景，并且主要针对枚举类型属性。字符串、数值、布尔值等普通类型暂不会生成属性值候选项。

## 开发

项目基于以下主要依赖：

- `.NET Framework 4.7.2`
- `Microsoft.VisualStudio.SDK 17.14.x`
- `Microsoft.CodeAnalysis.CSharp 5.3.0`

主要文件说明：

- `SilkyUISupportPackage.cs`：VSIX 包入口。
- `TestCompletionCommandHandler.cs`：接管编辑器输入命令，控制补全会话的触发、过滤、提交和关闭。
- `TestCompletionSource.cs`：注册 `.sui.xml` 内容类型并生成补全项。
- `XmlContextAnalyzer.cs`：根据光标位置判断当前处于标签名、属性名还是属性值上下文。
- `AttributeClassScanner.cs`：通过 Roslyn 扫描带 `XmlElementMappingAttribute` 的公开类及其属性。
- `SilkyUIMetadataService.cs`：提供补全用的查询接口和简单缓存。

开发调试时，建议准备一个包含 SilkyUI 控件定义和 `.sui.xml` 文件的测试解决方案，用来验证标签、属性和枚举值三类补全是否符合预期。

当前实现包含元数据缓存，但没有接入项目变更监听。如果 SilkyUI 类、属性或枚举发生变化，可能需要重新打开解决方案或重启 Visual Studio 才能看到最新补全结果。
