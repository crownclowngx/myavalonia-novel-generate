# 开发期 Codex 验证与后续模型隔离

日期：2026-09-12。依据用户补充要求：开发阶段优先使用 Codex 套餐的 GPT-6，DeepSeek 保持独立适配。

## 已核实的调用方式

- 本机 `codex login status` 显示 ChatGPT 登录。认证由 CLI 管理，项目不读取、复制或解析认证缓存。
- npm 路径的 CLI 0.140.0 被服务端拒绝使用 GPT-6；桌面应用附带 CLI 0.153.4 已完成真实调用。
- 模型参数为 `-m gpt-6-astra`，使用 `exec --ignore-user-config --ephemeral --sandbox read-only --json`。
- 最终代码审查由调用端提供明确的源码文本，通过 stdin 传入，模型不再读取本地文件或调用工具。
- 只读代码审查属于辅助验证，不替代单元测试、实际 SQLite 文件测试或作者对小说内容的评价。

官方依据：[Codex 非交互调用](https://learn.chatgpt.com/docs/non-interactive-mode)、
[ChatGPT 套餐认证](https://learn.chatgpt.com/docs/auth)、
[GPT-6 Astra 标识](https://developers.openai.com/api/docs/models/gpt-6-astra)。

## 调用记录与结论

首次旧 CLI 请求失败；新版 CLI 第一次只确认模型，第二次因子进程读文件被策略阻止而未完成审查，均不记为审查通过。
最终直接输入源码的调用完成审查，提出 4 项问题，已在 G0002 修复并补充回归。原始 JSONL、提示词和报告保存于被忽略的
`artifacts/G0002/`，避免将运行环境上下文混入代码提交。这里只记录不含凭据的结论。

这证明已登录 Codex 可用于当前开发验证，**不表示小说应用已接入生成模型**。G0005/G0008 后续将通过独立模型端口
隔离连接与协议：应用用例不依赖 Codex 进程参数或 DeepSeek HTTP DTO；开发替身不进入生产包。

DeepSeek 尚未发出真实请求，不声称连通性通过；实施其适配时以当时官方协议、明确连接与小范围请求验证为准。
不会把 ChatGPT 登录当作可任意使用的通用 API Key，也不把 Codex 套餐用量填写为零成本 API 调用。
