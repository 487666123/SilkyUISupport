# SilkyUISupport 架构

## 目标

SilkyUISupport 作为 Visual Studio VSIX 运行。每个编辑器功能必须使用同一份 XML 文档快照和 C# 元数据快照来解释 SilkyUI，确保补全、分类、诊断、悬停和导航不会产生矛盾结果。

项目保持为单个 net472 VSIX 程序集。目录表达逻辑边界而非程序集边界，这保留了现有的 MEF 导出、菜单资源和 VSIX 清单绑定。

## 分层

| 层 | 目录 | 职责 | 允许依赖的下层 |
| --- | --- | --- | --- |
| Visual Studio 宿主 | SilkyUISupport/VisualStudio | MEF 注册、内容类型、包、菜单命令、编辑器事件、宿主生命周期 | 所有内部层及 VS SDK |
| 编辑功能适配 | SilkyUISupport/Features | 将纯结果映射为 VS SDK 类型 | Semantics、Documents、Metadata、Infrastructure |
| 语义 | SilkyUISupport/Semantics | 解释元素、属性、成员、绑定、样式、源码符号 | Documents、Metadata |
| 文档 | SilkyUISupport/Documents | XML 树适配、源码范围、命名空间作用域、光标上下文、快照缓存 | GuiLabs.Language.Xml、VS 文本快照 API |
| 元数据 | SilkyUISupport/Metadata | Roslyn 扫描、属性描述符、不可变元数据快照和发布 | Roslyn、Visual Studio Workspace |
| 基础设施 | SilkyUISupport/Infrastructure | 弱订阅和运行时兼容性辅助类型 | 最小依赖 |

预期的依赖方向：

```text
VisualStudio -> Features -> Semantics -> Documents + Metadata
                         Features -> Documents/Metadata（仅快照和 SDK 映射）
Infrastructure -> Metadata
```

Documents 当前拥有 ITextSnapshot 缓存，因此仍使用 VS 文本 API。它尚未成为可独立发布的纯 XML 程序集；要提取它需要先将快照缓存移至宿主层。

## 请求流程

每个编辑器请求的流程：

1. 功能适配器接收当前 ITextSnapshot 加上光标位置或请求范围。
2. SilkyUIXmlDocument.Get 返回该快照对应的已解析模型。
3. SilkyUIMetadataService.GetSnapshot 读取一个不可变 C# 元数据快照。
4. 适配器创建 SilkyUISemanticModel(document, metadataSnapshot)；两个输入在整个请求中保持固定。
5. 纯服务产生 SilkyUICompletionItem、SilkyUIDiagnostic、SilkyUIClassification、SilkyUISymbolInfo 或 SilkyUINavigationTarget。
6. 适配器将这些标量结果映射为 Completion4、ErrorTag、ClassificationSpan、Quick Info 容器或 INavigableSymbol。

因此 Visual Studio SDK 类型不进入语义模型或其结果契约。字符范围是原始 XML 快照中的整数坐标。

## XML 文档

SilkyUIXmlParser 使用 GuiLabs.Language.Xml 并将语法树投影为编辑器需要的标签、属性、父级关系和原始源码范围。它处理零宽缺失 token、文档级恢复 token 和包装片段回退，不会暴露合成节点或错误偏移。

SilkyUIXmlSyntax 拥有 XML 侧规则：前缀到 URI 的作用域、遮蔽、sui:* 和 bind:* 指令识别，以及词法上的 Body/M.* 分类。它不决定 XML 名称代表哪个 C# 类型。

解析器详情和人工验证矩阵见 [XML 解析](xml-parsing.md)。

## 元数据快照生命周期

当 Workspace 变化时，SilkyUIMetadataService 标记元数据过期。刷新操作捕获 Workspace.CurrentSolution 一次，将同一 Solution 传给所有 Roslyn 扫描，本地构建映射类、Body 类、样式属性、目标类和目标属性，然后通过 Interlocked.Exchange 发布一个 SilkyUIMetadataSnapshot。

失败的刷新永不发布部分状态；之前的完整快照保持可用。每个功能读取 GetSnapshot 一次，因此请求不会混合来自不同刷新代际的值。

## 语义模型

SilkyUISemanticModel 是唯一的 SilkyUI 规则入口点：

- 解析 Body sui:Class，优先全限定类名而非短名。
- 解析映射元素、sui:Style，以及从 Body 或映射元素开始逐层嵌套的 M.* 成员。
- 分类普通、绑定、命名空间、指令和不支持前缀的属性。
- 应用统一的 getter/setter/static/indexer/引用类型资格规则。
- 产生元素、成员和属性的符号结果，供悬停和导航使用。

成员解析保留目标类型的公开可读实例属性，包含基类及继承接口的属性，供下一层 M.* 查找使用；对象展开不要求 setter，普通赋值和绑定要求公开 setter。补全、诊断、分类、悬停、导航及颜色预览共享这一解析结果。

支持 XML 元素逐层嵌套，不支持单个标签内的点分成员路径（例如 M.Appearance.Border）。中间对象需要事先初始化，成员展开仅支持 class 或 interface，不处理结构体逐层写回。绑定仅解析目标侧属性名；样式属性目录仍使用可读属性。

## VSIX 约定

SilkyUISupport.csproj 使用 SDK 递归源码包含。移动 C# 文件不需要显式 Compile 条目。Menus.vsct、source.extension.vsixmanifest 和项目文件保留在内层项目根，因为 VSIX 和 VSCT 构建直接引用它们。

构建命令：

```powershell
dotnet build SilkyUISupport\\SilkyUISupport\\SilkyUISupport.csproj --no-restore
```

构建后，应在 Visual Studio 中验证补全提交、命名空间遮蔽、不完整 XML 恢复、成员导航、样式 sui:Target 和元数据刷新行为。
