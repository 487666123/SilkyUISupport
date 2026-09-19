# GuiLabs.Language.Xml 接入说明

## 当前状态

- VSIX 已接入 `GuiLabs.Language.Xml` 1.2.120，使用 `Microsoft.Language.Xml.Parser` 生成语法树。
- 包许可证为 Apache-2.0；net472 项目使用包提供的 netstandard2.0 程序集。
- `SilkyUIXmlParser` 从语法节点投影标签、属性、原始字符范围和父元素关系；原来的逐字符扫描及标签匹配栈已移除。
- `SilkyUIXmlDocument` 按 `ITextSnapshot` 全量解析并缓存投影结果，保留局部标签查询、样式名查询和父元素查询接口。补全、颜色、错误检查、Quick Info 和导航共享这些结果。

## 职责边界

- 库负责 XML 词法和结构：名称、属性、引号、注释、CDATA、处理指令、开始／结束标签和语法恢复。
- 适配层负责命名空间作用域与前缀遮蔽；库的 `GetAttribute(localName, prefix)` 不代替按 URI 判断 SilkyUI／Binding／Properties 命名空间的规则。
- `XmlContextAnalyzer` 继续负责光标处的补全上下文。完整标签使用全部声明；未完成标签只使用光标前可见的声明。
- `sui:*`、`bind:*`、`Body`、`prop:*`、C# 元数据、属性绑定和现有语义诊断仍由 SilkyUISupport 负责。普通属性值保留原始文本及其长度；命名空间值沿用已有解码策略。
- 迁移范围为 SilkyUISupport。`SilkyUIAnalyzer` 继续使用 `XDocument`。

## 字符范围与恢复

- 使用 token 的 `SpanStart`／`Width` 区分原始文本和附带空白；缺失的标点可能是带 trivia 的零宽 token，不能用 `FullWidth` 判断它是否实际存在。
- 只投影真实的开始标点，忽略库合成的开始／结束标签。成对成员标签的开始和结束名称指向同一个父元素，结束标签继承开始标签的命名空间作用域。
- 文档根节点之后的额外元素或未完成 `<` 可能位于文档级跳过 token 中；适配层用临时根包装其后缀进行片段解析，将偏移换算回原始快照，并检查恢复进度。
- 某些位置错误的文档声明会使库丢失原始文本宽度。适配层检查全文宽度，必要时对全文使用片段回退；无法保持原始范围时不发布偏移后的标签。

## 验证方式

使用 `dotnet build` 检查编译；需要验证语义变更时，可在 obj 目录通过临时程序调用当前源码，不引入单元测试项目。编辑器界面的实际交互仍需在 Visual Studio 中手动确认。

本次 Windows 下的 net472 VSIX 构建通过，零警告、零错误。VS 宿主中的实际交互仍需人工确认，重点场景包括：

- 输入 `<`、`</`、`<prop:`，以及带缺失值或未闭合引号的属性；检查光标处的补全和替换范围。
- 切换命名空间前缀，覆盖祖先声明，在声明尚未完成时请求补全；确认严格的 URI 匹配和前缀遮蔽。Properties URI 为 `https://github.com/487666123/SilkyUIFramework/Properties`，推荐 `prop`，也应确认 `<p:` 使用实际声明、空 `<` 提供当前作用域内属性候选，以及默认 Properties 命名空间的无前缀节点不会混入控件别名。
- 在注释、CDATA 和处理指令附近编辑，确认其文本不会被当成普通元素。
- 检查普通元素及逐层嵌套的 `prop:Xxx` 成员：只有 getter 或 setter 不公开的对象属性应可继续展开，叶子属性赋值仍要求公开 setter。
- 在嵌套成员中检查不完整 `<prop:` 的补全、属性和枚举补全、基类与继承接口成员、只读赋值诊断，以及悬停和开始／结束标签导航。
- 在嵌套成员的 Color 属性上检查颜色预览、点击选色和回写。
- 在根元素后继续输入标签，确认恢复后的补全和字符位置正确。
