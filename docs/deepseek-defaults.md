# DeepSeek 默认连接与参数选择

适用插件版本：1.0.1。核对日期：2026-09-12。

## 普通用户操作

打开“模型连接”，点击“新连接”，默认就是 DeepSeek。名称、地址及规划/正文/检查三个预设均已填好；只需输入 API Key，再点击“保存连接（配置与 Key）”。密钥保存后输入框清空，不回显原值；默认只保留本次会话，需要重启后继续使用时勾选“使用 Windows 当前用户加密保存”。

回到作品“设定 → 本书模型连接”，刷新、选择该连接并明确绑定。已有作品不会因为工具默认值变化而自动换模型。

| 项目 | 新连接默认值 | 可选或补充说明 |
| --- | --- | --- |
| 名称 | DeepSeek 默认连接 | 可自行命名 |
| 地址 | https://api.deepseek.com | 使用 Chat Completions 协议 |
| 规划、正文、检查模型 | deepseek-flash | 下拉可选 deepseek-v4-pro，自定义 ID 在折叠区填写 |
| 思考与推理 | high，思考开启 | none 关闭；low / high / max 开启不同强度 |
| 各任务输出上限 | 65536 token | 包含思考和正文；工作台仍限制在 256–131072 范围 |
| API Key | 空 | 用户填写；没有预置、复制或读取真实密钥 |

此输出上限对应官方 high 思考模式默认值，是单次请求容量，不是每次实际消耗，也不是本轮生成预算。改选 none/max 时保留作者明确设置的输出上限；官方省略该参数时的默认值可能不同。

旧连接原样读取，模型、端点和已有凭据不自动迁移。要采用新版预设，点击“恢复当前服务商默认参数”后保存；端点发生变化时，旧密钥不会沿用到新端点。主动切换服务商会重置该表单的模型参数并清空未提交 Key，返回已保存配置可用“放弃未保存更改”。

## 官方来源与协议对应

[模型与地址](https://api-docs.deepseek.com/quick_start/pricing/)列出当前模型 ID 和基础地址；这里选 Flash 作为新连接默认，并保留 Pro 选项。
[思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)说明开关与强度；[Chat Completions 参数](https://api-docs.deepseek.com/api/create-chat-completion/)说明默认输出容量及 JSON/SSE 请求。

请求明确发送 `thinking.type` 与 `reasoning_effort`。none 对应 disabled；low/high/max 对应 enabled。旧连接 medium 按官方兼容规则映射成 high，不改写旧配置。temperature、top_p 和惩罚项继续省略，由服务端采用默认行为，避免将思考模式下不生效的温度作为可用调优入口。

## 设计与验证记录

ConnectionDefaults 集中保存新建草案默认值；PresetEditor 管理界面选择；ConnectionSettings 按服务商验证；DeepSeekTextModel 负责协议映射。保持原有三字段预设结构，不迁移项目数据库；Codex 仍限制 low/medium/high。

真实 Avalonia 下拉框在更新选项时会回写旧选择；采用一次替换选项和更新期间拒绝回写，防止切换到 Codex 后仍留下 DeepSeek 模型。已有自定义模型加入当前选项，不因打开工具而丢失。

新增测试覆盖默认草案不自动落盘、只填 Key 一次保存、旧配置与自定义值保留、切换服务商、密钥保存失败与新输入竞态、五种 DeepSeek 推理协议组合和真实下拉绑定。Debug 与 Release 各 288 项测试通过，Release 构建 0 警告、0 错误；见 [1.0.1 替换部署记录](deployments/2026-09-12-deepseek-defaults.md)。

本次没有真实 DeepSeek Key，因此使用受控 HTTP/SSE 验证字段、鉴权隔离和响应处理，不声称已经完成远端连通测试；界面可由用户主动生成一句检测。
