# G0035 实施方案

`TemplateLibraryTool` 增加可选分析面板，既有一个 Document、两个 Tool 的注册保持兼容。面板使用生产导入、运行、连接和报告服务，无需创作项目。三个原生页签覆盖导入、运行和报告，长列表及文本只绑定有界内容。

`NovelAnalysisActivity` 在根作用域持有运行、暂停控制和取消令牌；同一运行重复开始共用同一任务，不因控件移除或书目切换停止。进度先在后台读取账本形成快照，再回到面板的 UI 上下文。报告阅读状态单独放入 `NovelReportReader`，导出通过窄 `IAnalysisReportWriter` 端口，以同目录临时文件原子提交到新路径。

`NovelAnalysisCandidateService` 只读复核候选，复用实际请求构造器和契约给出本地校验原因。继续、重试及本地采纳均沿用原账本，失败/未知费用需显式确认；补充约束仅作用于失败节点。崩溃遗留 Running 先进入原操作账本核对，不直接生成新操作 ID 重发。

关闭预检查先取消短操作、等待导出和定位，再排空模型请求；若其他草案阻止退出，排空不永久禁用分析服务。最终 Shutdown 与同步 Provider Dispose 通过 `PluginCloseCoordinator` 协调。面板和 Activity 同时实现同步与异步释放，同步入口只启动受跟踪关闭任务，不阻塞 UI。

原生回归发现 ComboBox 在集合 Replace 时会把选择暂时回写为空；进度刷新按更新前的运行 ID 恢复选择，避免报告入口丢失当前运行。Host 组合实测发现仅实现 IAsyncDisposable 的服务不能由宿主同步释放，修复后整套组合退出通过。详见[原生流程与 Host 验证](native-analysis-workflow.md)。
