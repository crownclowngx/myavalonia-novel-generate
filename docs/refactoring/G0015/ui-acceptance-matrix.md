# G0015 UI 验证矩阵

日期：2026-09-12。原生 Avalonia Headless/Skia，实际业务 View 与 ViewModel；不把它称为完整桌面 Host 验证。

| 场景 | 证据 | 当前结果 |
| --- | --- | --- |
| 中文正文、切章、自动保存及规则证据定位 | NativeViewTests；workspace-1200-light.png / workspace-800-dark.png | 通过 |
| 实体与规划中文输入/保存/视图重建 | story-context.png / planning.png | 通过 |
| 候选显示、选区定位、改写与修订接受 | chapter-generation.png；原生选区测试 | 通过 |
| 三章连续运行及预算状态 | continuous-run.png | 可控模型通过 |
| 短材料证据、模板与本书采用 | material-calibration.png | 通过 |
| 800×700，150% 缩放，长文本和侧栏切换 | narrow-inspector-150.png；2000 行正文保存 | 通过 |
| 连接预设输入与密钥遮蔽 | 原生连接表单测试 | 通过 |
| 七命令声明、双书目标隔离、取消传递 | WorkbenchTests / CompositionTests | 通过 |
| 关闭重入、忙碌切章、摘要保留 | WorkbenchTests | 回归通过 |
| 真实 Host 菜单/Dock/系统选择器/退出 | G0016 单独登记 | 本阶段未用 Headless 替代 |

截图在 artifacts/G0015/ui。实际多显示器 DPI 切换、输入法候选窗与系统文件选择器仍需桌面 Host 交互证据。
