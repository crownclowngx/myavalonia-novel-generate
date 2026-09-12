# G0005 实施方案

基线 G0004 `43a7c10`。实现 FR-03 的配置基础，对应 A01/A04/A06/A15/A16 的本地隔离部分。

## 职责与方案

`Domain/ModelConnection.cs` 表达连接身份、配置版本、授权代数、三个任务预设和本书绑定，不依赖网络或加密。
`Application/Connections/ConnectionService.cs` 处理配置、默认项、绑定检查、冻结和凭据用例，依赖两个窄端口。
`ConnectionStore` 保存普通配置；`UserCredentialVault` 保存会话秘密或 Windows 用户密文。
两个存储各自负责其持久化机制，不创建把密钥和业务数据混在一起的通用仓储。

共享 `ModelConnectionsTool` 管理表单，保存后清空本次提交的秘密输入。保存期间出现新输入则保留并阻止关闭。
查询期间拒绝切换连接，异步结果复核身份与版本，避免展示串线。`MainDocument.Connections.cs` 直接调用服务，
可以在从未打开 Tool 的情况下读取绑定、显示缺失状态、解除及明确重绑。

## 数据与生命周期

目录 schema 1 兼容增加 model_connections / connection_preferences 表。作品 schema 4 增加 ConnectionBinding，
旧 1/2/3 先 SQLite 一致性备份再事务迁移。恢复文件写格式 4，兼容读取 1–4。
作品只保存连接 ID、版本和名称；不保存 Endpoint、CLI 路径或 Key。

新书创建时读取默认建议，旧书始终保持原绑定；配置更新后旧版本拒绝请求冻结，要求作者核对并重绑。
冻结对象保存本书身份、完整非秘密连接快照、用途及对应预设；凭据只在实际发送前从当前授权槽位取得。

## 依赖与验证

新增锁定 `System.Security.Cryptography.ProtectedData` 10.0.0，复用本机已有稳定包并声明私有部署资产。
实际开发暂存目录有 11 个文件，包含 DPAPI 程序集与 win-x64 SQLite；独立 ALC SQLite 探针通过。
这只是 Debug 开发资产检查，没有生成发行包或运行发布门禁。

专项见 [连接与凭据契约](connection-and-credential-contract.md)。原生控件测试验证中文名称、NumericUpDown 与隐藏密钥框。
模型审查使用已登录 Codex 套餐的 `gpt-6-astra`，三个并发问题经修改与回归验证。
