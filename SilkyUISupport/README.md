# SilkyUI Support

`SilkyUI Support` 是一个面向 Visual Studio 2022 的 VSIX 扩展，用来提升 `SilkyUI` 的 `.sui.xml` 编写体验。

## 功能

- 为 `.sui.xml` 文件提供标签名、属性名、枚举属性值补全
- 根据当前解决方案中的 C# 类型动态生成补全内容
- 为 `Body` 的 `Class` 属性补全 `UIElementGroup` 子类，并补全对应类的公开属性
- 将鼠标悬停在已映射的元素或属性上时，显示对应的 C# 类型、声明类型和枚举值等信息
- 支持从 XML 元素名和属性名跳转到对应的 C# 类或属性定义
- 实时检查未知元素、未知属性、重复属性、非法枚举值和无效的 `Body Class`，并显示错误波浪线
- 在 C# 编辑器中右键类名时，提供“创建 SilkyUI XML 初始模板”菜单
- 自动创建与类同名的 `.sui.xml` 文件，并写入初始模板

生成的模板示例：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<!-- Class 填写对应类名 -->
<Body Class="SilkyUIFramework.UserInterfaces.MouseMenuUI">
</Body>
```

## 使用

1. 打开包含 SilkyUI 相关 C# 项目的解决方案。
2. 编辑 `.sui.xml` 文件时，直接输入 XML 内容即可触发补全。
3. 在 C# 文件中将鼠标放到类名附近并右键，选择“创建 SilkyUI XML 初始模板”。

## 安装与构建

- Visual Studio 2022 17.14 或更高版本
- 64 位 Visual Studio
- .NET Framework 4.7.2 开发环境

使用 Visual Studio 打开 `SilkyUISupport.slnx`，构建后安装生成的 `.vsix` 文件即可。
