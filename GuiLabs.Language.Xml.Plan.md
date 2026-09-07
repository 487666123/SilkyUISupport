# GuiLabs.Language.Xml 接入规划

## 当前状态

- `GuiLabs.Language.Xml` 已作为未来接入的预留依赖保留，目前没有用于解析。
- 当前 XML 编辑功能使用自己的容错扫描、命名空间作用域解析和快照缓存。

## 接入目标

仅用于 **SilkyUISupport（VSIX）**，评估用该库替换手写 XML 语法扫描，处理编写中的不完整 XML。`SilkyUIAnalyzer` 继续使用 `XDocument`，不在迁移范围内。

## 后续步骤

1. 确认许可证、net472 与 Visual Studio 宿主兼容性，以及未闭合标签、属性引号、文本范围的实际支持情况。命名空间作用域是否需要自行适配也要确认，不假设库自动提供。
2. 在统一文档层适配该库，保留按 `ITextSnapshot` 缓存和局部标签查询接口，避免每个编辑功能独立解析。
3. 让补全、颜色、错误检查、Quick Info 和导航逐步读取同一份解析结果，再移除被替代的手写扫描代码。

## 保留的业务规则

`sui:*`、`bind:*`、`Body`、`M.*`、C# 元数据与属性绑定语义仍由 SilkyUISupport 负责；命名空间补全保持严格策略。

修改完成后只运行 `dotnet build` 检查编译；编辑器行为按需在 Visual Studio 中手动确认，不引入单元测试项目。
