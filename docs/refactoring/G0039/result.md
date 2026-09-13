# G0039 实施结果

日期：2026-09-13。状态：本地已验证，5/5 工作项实现。

容量预检、仅问题提取单元的局部拆分、预算及深度边界、父子历史持久化、界面容量说明和旧运行兼容已实现。没有增加全过程预算，也没有重新发送用户的小说。

## 已完成验证

最终 tools/verify-local.ps1 全部通过：锁定还原、Debug 构建 0 警告/错误、402 项测试全部通过、代码格式校验、456 个文档链接和 Markdown/HTML 来源哈希通过。本阶段相对 G0038 增加 14 个测试用例。

- 局部拆分与格式回显专项 10 项通过：覆盖字符与 Unicode、未知费用、请求及 token 预算、深度上限、暂停重开、事务失败后恢复，以及 json_object 格式定义回显。
- 修复专项此前 30 项通过，包括完整 JSON 后断流拒绝采纳、超过旧 8000000 字符封装上限的正常长流、五阶段全流程及完成报告修订缓存复用。
- 最终补测覆盖“继续运行”入口：拆分写库失败后保留父操作 ID，直接重开与按钮恢复均不重发父请求；schema1、schema2 均验证先备份再升级且只备份一次。
- 用户当前运行只读回放：51071 字符、20 个单元，前三个成功节点的输入指纹和结果哈希一致；6 次历史请求保守累计仍为 237453 token。
- 三份失败响应分别识别为 SchemaEcho、结构字段通过、InvalidJson。“结构字段通过”不证明原文证据或流完整，也未自动采纳。最后一份未知用量没有清零。
- 实际 Host Provider/Registry/Activator 与插件对象图联调通过；旧报告可读取、定位证据和导出，五阶段及拆分参数绑定通过，单次容量双向绑定通过，关闭后取消检查点保留。系统文件选择器与模型使用替身，未冒充完整桌面 Dock 人工验收。
- Host 和 AnalysisProbe 构建曾同时写入共享 obj，产生一次 PDB 文件占用；串行重跑 Host 构建及联调已通过。

## 可重放证据

本机证据位于忽略目录 artifacts/G0039/replay-final 与 artifacts/G0039/host-parameters。前者只有离线副本及脱敏统计，后者包括报告和参数界面截图；不把小说原文及数据库提交到 Git。

重放命令（输出目录必须新建）：

```powershell
dotnet run --project tools/AnalysisProbe/AnalysisProbe.csproj -c Debug --no-restore -- replay '<用户分析工作区>' '<新的离线回放目录>'
$env:NOVEL_HOST_PROBE_OUTPUT = '<新的 Host 验证目录>'
$env:NOVEL_ANALYSIS_SAMPLE_ROOT = '<已有完成报告的独立样本目录>'
dotnet run --project tools/HostProbe/HostProbe.csproj -c Debug --no-restore -p:NovelHostRoot=D:/code/local/avalonia_dock_simple_test -warnaserror
./tools/verify-local.ps1
```

本次新增网络调用为 0，未读取密钥、未改写正式用户库、未部署。真实 DeepSeek 新参数下的文学质量和完整桌面体验仍需后续实测，原 G0035/G0036 待验收项保持不变。
