# G0005 连接、凭据与开发模型边界

## 配置身份

连接 ID 独立于名称，同名连接不共享认证。Version 每次配置保存递增；CredentialEpoch 只在服务商或授权目标变化时更新。
重新改回旧端点也产生新代数。规划、正文、检查分别保存模型 ID、输出上限与推理强度，运行冻结选定任务预设。
本阶段只建立配置，不把“已保存/已配置”标为“远程可用”。

新书默认只是创建时的建议。已有作品不因全局默认变动而重绑；搬迁缺失连接时继续离线编辑，由作者明确选择本机连接。
配置升级要求重新核对绑定；旧冻结配置在发送前会被版本检查拒绝。

## 秘密存储

| 类型 | 保存位置 | 生命周期 |
| --- | --- | --- |
| Codex 登录 | CLI 自己管理 | 插件不读取、复制或解密认证缓存 |
| API 会话 Key | Vault 内存字节数组 | 释放时清零；下次启动需输入 |
| API 持久 Key | `Credentials/<连接ID>-<授权代数>.bin` | Windows CurrentUser DPAPI；仅当前用户环境可解密 |
| 本书绑定、模板、目录配置 | 非秘密业务存储 | 不含 Key；恢复与导出不能打包 Credentials 目录 |

DPAPI 附加熵含连接 ID、授权代数与规范化端点。不同槽位的旧进程写入/删除不能破坏新版 Key。
端点必须 HTTPS，禁止 UserInfo/Query/Fragment；参数不通过端点夹带。写密文先落临时文件并 Flush，再原子替换。
明文字符串仅出现在输入及请求边界；已有 Key 不回显，不记录其末尾字符或值。普通 JSON 冻结对象没有秘密字段。

清除作用于当前授权槽位，阻止后续获取该授权的 Key；已经发送的请求不能撤回。历史授权槽位可能保留旧密文，
不会被当前配置选择；其物理清理由后续凭据维护处理，业务备份始终排除整个 Credentials 目录。
会话替换清除同槽位旧持久密文，重启不会复活旧持久 Key。删除失败不能报告清除成功。
损坏、搬迁或无法解密显示“不可用”，可重新输入或清除，不能阻止正文读取。

## Codex 与 DeepSeek 隔离

开发验证使用用户已有套餐和 `gpt-6-astra`。本机已验证 CLI 0.153.4；旧 0.140.0 被服务端拒绝，不能当作成功。
可执行路径由本机明确配置，不能从作品文件提供命令或参数。后续适配通过 ProcessStartInfo.ArgumentList 和 stdin，
不拼接 shell、不允许模型执行工具或任意本地操作。当前 CLI 路径不会自动写入可迁移作品。

DeepSeek 首次调用资料复核于 2026-09-12：[官方快速开始](https://api-docs.deepseek.com/)提供 API 根地址与 Bearer 认证，
当前示例模型为 deepseek-flash。模型 ID 可配置，旧设计中的历史型号不作固定承诺；真实协议与错误/取消在 G0008 验证。
Codex 的非交互入口参见 [官方说明](https://learn.chatgpt.com/docs/non-interactive-mode)。Windows 用户加密语义参见
[Microsoft DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-10.0)。

## 当前限制

尚未实现连接远程检测或小说请求。Key 测试全部使用虚构值与隔离目录；没有真实 DeepSeek 调用。
本阶段预设还不是远端能力证明；G0008 必须显式处理各协议支持范围，不能悄悄忽略预算、用途和参数。
真实 Host 的 Tool 隐藏与卸载待联调；Standalone 的两个 Tool 页仅验证插件对象图与局部关闭逻辑。

G0006 当前作品 schema 为 5，规则新增字段和证据语义见 [G0006 契约](../G0006/writing-rule-contract.md)。
