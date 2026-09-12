# 模型协议与能力记录 v1

复核日期：2026-09-12。此记录描述实际适配器边界；历史产品设计中的型号不作为硬编码约束。

| 能力 | Codex 套餐适配器 | DeepSeek 适配器 |
| --- | --- | --- |
| 认证 | 已登录 CLI 自行处理 | 每次读取本连接当前凭据，单请求 Bearer |
| 当前验收对象 | 本机 CLI 0.153.4、`gpt-6-astra` | 官方 Chat Completions SSE 契约；未做真实 Key 调用 |
| 传输 | `codex exec --json` 的 JSONL | HTTPS `POST /chat/completions`，SSE |
| 正文 | 单条完成的 `agent_message`；非逐 token 流 | `choices[0].delta.content`，累计正文节流通知 |
| 推理 | 忽略 reasoning 项 | 忽略 `reasoning_content`，不写入正文或账本 |
| 结构输出 | 有任务 Schema 时传 `--output-schema` | JSON object 模式，Schema 作为任务约束发送 |
| 本地校验 | 两者均校验非空对象、重复字段、深度及任务字段边界 | 同左，JSON 模式不替代业务检查 |
| 输出上限 | 软提示 + 完成后的 usage/字符检查；不能承诺远端硬截断 | 发送 `max_tokens`，接收 `length` 时保留截断候选 |
| 缺失用量 | null，保留预留额 | null，保留预留额；申请 `include_usage` |
| 多轮/工具 | 本阶段不支持 | 请求不提供工具，不接受 tool_calls |

Codex 官方支持非交互 JSON 事件与输出 Schema，默认复用 CLI 登录。见
[非交互模式](https://learn.chatgpt.com/docs/non-interactive-mode)。
本机参数依据实际 `exec --help`、`features list` 和
[配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)核对。

## Codex 的隔离与兼容

适配器开启 read-only、never，忽略个人配置并关闭 hooks、plugins、apps、shell/unified exec、Code Mode 宿主、
浏览器、计算机、图片、记忆、目标、子代理、技能搜索和依赖安装等能力；关闭 web_search，工作目录不指向作者项目。
关闭参数固定在适配器中，不给模型指定或修改。遇到命令/MCP/搜索等非文本事件立即停止；这属于纵深检查，不能替代底层禁用。

CLI 0.153.4 在 Code Mode 宿主关闭时先返回一条 `item.completed/error` 诊断。保留执行能力关闭，忽略诊断原文，
仍必须收到唯一完整正文、`turn.completed` 和退出码 0 才算成功。不会因诊断去开启工具。
此行为已在本机真实样本验证；其他 CLI 版本尚未验收，更新后需重跑兼容与工具边界验证。
插件不是独立操作系统安全沙箱，也不声称能覆盖未来 CLI 新增能力。

Codex 没有在本阶段核实到可承诺精确硬输出上限的稳定 CLI 参数。
本地字符目标为 `MaxOutputTokens × 4`，绝对正文容量为 100 万字符；超出目标保留候选并标截断，绝对容量以上只保留有界前缀。
这不代表真实 token 换算，也不能阻止已经发生的服务商消耗。连续运行不得把它视为硬计费上限。

## DeepSeek 官方契约

端点默认可填 `https://api.deepseek.com`，模型名称由连接保存。当前官方列出 `deepseek-flash`（V4.1-Flash）与
`deepseek-v4-pro`（V4-Pro-0813）；均支持文本、思考和 JSON 输出，当前最大输出 384K，插件主动限制预设至 131072。
请求发送 `reasoning_effort`；官方当前接受 medium 并映射到 high。详见
[Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)。

以下价格仅为本次官方页面的美元/百万 token 快照，不作为自动扣费、人民币换算或账号账单：

| 型号 | 峰时缓存命中输入 | 峰时未命中输入 | 峰时输出 |
| --- | --- | --- | --- |
| deepseek-flash | 0.006 | 0.30 | 1.20 |
| deepseek-v4-pro | 0.044 | 1.32 | 3.96 |

页面注明非峰时价格减半，峰时为工作日 UTC 01–04 与 06–10；实际费用以服务商账单和
[官方模型价格页](https://api-docs.deepseek.com/quick_start/pricing/)为准。Codex 套餐不套用 API 单价；本阶段记录 token，不推算套餐剩余额度或人民币成本。

## 验证边界

Fake HTTP 覆盖认证、余额、限流、重定向响应和中断；不把接口契约测试写成真实 DeepSeek 连通。
GPT-6 小样本仅证明当前账号、型号和参数能够返回文本，不证明长篇质量、全部型号或完整 Host 工作流。
