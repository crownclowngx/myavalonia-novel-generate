# 普通用户说明书维护记录

日期：2026-09-12。基于 G0016 工程候选 `c4eba95`，本次只新增说明书、图片资产、渲染脚本和文档校验，不改变创作功能。

## 交付物与用途

- [用户说明书 HTML](novel-workbench-user-manual.html)：普通作者直接打开阅读，五条使用路线、六张实际界面截图、目录跳转、图片放大、打印样式；正文和图片均内嵌，无外部网络依赖。
- [Markdown 原文](novel-workbench-user-manual.md)：唯一正文来源；修改后运行 `python docs/product/render-user-manual.py`。
- [渲染脚本](render-user-manual.py)：复用产品文档的受限 Markdown 渲染函数，使用独立排版；不执行产品需求页构建，不引入第三方依赖。

## 内容核对

按钮、输入范围及操作路径以 MainView、MaterialCalibrationView 和对应 ViewModel 为依据；边界以 ChapterGenerationRules.Preflight、Assess、MaterialRules.Adopt 和生成服务为依据。

特别核实并明示：短材料不是模型训练；材料读入不是小说章节导入；提炼只采用文风/方法，不自动填世界观与故事实体；外部旧稿需逐章粘贴、有效章纲与顺序复核；末尾续写从整章末尾追加；目标字数用于整章；上下文预览容量不改变生成服务策略；未检查人工稿与已通过审校稿不能混称。

六张截图来自 G0015 实际 Avalonia 本地界面渲染：workspace-1200-light、planning、material-calibration、story-context、chapter-generation、continuous-run。首张复用既有产品资产，其余五张原样复制到 assets/manual；没有用概念界面替代真实截图，未修改截图像素。页内说明明确标注测试正文与用量不代表文学效果。

## 验证方式

运行 `python tools/check-docs.py` 校验 Markdown 链接及两份 HTML 来源哈希；检查 HTML 的内部锚点、内嵌图片、无远程资源。浏览器核对宽窗/窄窗布局、图片加载、目录导航、图片放大与关闭、打印布局以及控制台错误。

本次不重复付费样稿，不运行与文档无关的全套软件单元测试，不使用 Windows CI 或发布门禁。G0016 的软件测试与验收结论保持原范围。

## 本次验证结果

- 文档校验通过：310 个本地 Markdown 链接，产品需求与用户说明书两份 HTML 的来源哈希一致。
- HTML 结构检查通过：9 个阅读章节，内部锚点均可解析，ID 无重复，6 张图片均内嵌并含文字说明，无远程资源依赖。
- 浏览器 1440×1000 与 390×844 检查通过：图片全部载入、页面无横向溢出、宽屏及移动目录可以定位目标章节；修正了移动目录收起时的锚点定位顺序。
- 图片放大、关闭按钮、Esc 关闭通过；关闭事件完成后键盘焦点返回原图片按钮。浏览器控制台无错误或警告。
- 打印按钮调用浏览器打印接口；打印 CSS 单独检查通过，隐藏导航与工具栏并限制截图高度。本次未生成或验收分页 PDF，也未执行物理打印。
