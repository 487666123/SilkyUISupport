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

SilkyUIXmlSyntax 拥有 XML 侧规则：前缀到 URI 的作用域、遮蔽、sui:* 和 bind:* 指令识别，以及 Body/Properties 属性节点分类；根节点声明 sui:Class 时也按根对象处理。它不决定 XML 名称代表哪个 C# 类型。文档保留编辑器文件路径用于 CLR 项目归属，另存为导致路径变化时会使文档缓存失效。

解析器详情和人工验证矩阵见 [XML 解析](xml-parsing.md)。

## 元数据快照生命周期

当 Workspace 变化时，SilkyUIMetadataService 标记元数据过期。刷新操作捕获 Workspace.CurrentSolution 一次，将同一 Solution 传给所有 Roslyn 扫描，本地构建映射类、Body 类、样式属性、目标类和目标属性，然后通过 Interlocked.Exchange 发布一个 SilkyUIMetadataSnapshot。

CLR 导入另外构建按项目区分的 Compilation 和候选命名空间索引，覆盖当前程序集、通过 global 别名可见的引用程序集及类型转发。命名空间去重，候选类型名经 C# global:: 语义绑定和构造资格检查后才用于补全；类型解析也使用同一绑定路径，不支持 assembly 参数或 extern alias 标签。XML 归属优先匹配 Documents/AdditionalDocuments，回退取最长项目目录，遇到歧义不选择任意项目。索引与其他元数据在同一刷新代际发布，补全查询不重新扫描解决方案。

失败的刷新永不发布部分状态；之前的完整快照保持可用。每个功能读取 GetSnapshot 一次，因此请求不会混合来自不同刷新代际的值。

## 语义模型

SilkyUISemanticModel 是唯一的 SilkyUI 规则入口点：

- 解析 Body sui:Class，优先全限定类名而非短名。
- 解析映射元素、CLR 导入元素、sui:Style，以及从根对象或普通元素开始逐层嵌套的 prop:* 成员。
- CLR 类型查找与创建资格检查使用 XML 所属项目和 sui:Class 访问上下文；已解析类型复用属性、诊断、悬停和导航结果。根类已知时选取与 Analyzer 相同的带类体的源码声明进行 global:: 推测绑定，保留 file 局部类型遮蔽时保守拒绝的限制。
- 根类补全按最外层开始标签位置及 Class 指令判断，不依赖已完成的属性值或 Body 分类；此编辑规则不改变诊断、悬停等功能的标签分类。
- 分类普通、绑定、命名空间、指令和不支持前缀的属性。
- 应用统一的 getter/setter/static/indexer/引用类型资格规则。
- 产生元素、成员和属性的符号结果，供悬停和导航使用。

Properties 属性节点通过作用域解析后的 URI 判断，不硬编码前缀，属性名取 localName；支持默认命名空间和嵌套覆盖。补全枚举可见声明，空 `<` 同时提供属性节点候选，输入冒号或删除前缀后重建列表；默认 Properties 命名空间不提供无命名空间控件别名。

成员解析保留目标类型的公开可读实例属性，包含基类及继承接口的属性，供下一层 prop:* 查找使用；对象展开不要求 setter，普通赋值和绑定要求公开 setter。补全、诊断、分类、悬停、导航及颜色预览共享这一解析结果。

支持 XML 元素逐层嵌套，不支持单个标签内的点分成员路径（例如 prop:Appearance.Border）。中间对象需要事先初始化，成员展开仅支持 class 或 interface，不处理结构体逐层写回。绑定仅解析目标侧属性名；样式属性目录仍使用可读属性。

## VSIX 约定

SilkyUISupport.csproj 使用 SDK 递归源码包含。移动 C# 文件不需要显式 Compile 条目。Menus.vsct、source.extension.vsixmanifest 和项目文件保留在内层项目根，因为 VSIX 和 VSCT 构建直接引用它们。

构建命令：

```powershell
dotnet build SilkyUISupport\\SilkyUISupport\\SilkyUISupport.csproj --no-restore
```

构建后，应在 Visual Studio 中验证补全提交、命名空间遮蔽、不完整 XML 恢复、成员导航、样式 sui:Target 和元数据刷新行为。
