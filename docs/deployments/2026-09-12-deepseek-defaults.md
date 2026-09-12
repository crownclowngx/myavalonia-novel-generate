# 2026-09-12 DeepSeek 默认连接更新与替换部署

源码提交：`b9f308d`。插件版本：1.0.1，Release，win-x64。

按用户要求直接替换 `D:\data\avalonia\Controls\NovelGeneratePlugin`。默认连接、模型选择、推理开关和密钥保存操作见 [DeepSeek 参数说明](../deepseek-defaults.md)。

## 验证与部署结果

- Debug、Release 各 288 项测试通过，0 失败、0 跳过；Release 构建 0 警告、0 错误。
- 格式校验、Markdown 链接和产品/说明书 HTML 来源哈希校验通过。
- 原生界面验证默认选项、切换服务商、API Key 遮蔽以及真实下拉双向绑定，截图已更新到说明书。
- 替换前宿主已退出；目标及内部路径经过归属、重解析点检查，旧插件 11 个文件完整备份并逐个校验。
- Build 包的 DeployManagedPlugin 目标部署 11 个文件，manifest 版本为 1.0.1，插件 DLL 摘要与 Release 输出一致。
- 对部署目录运行独立加载上下文探针：SQLite 原生依赖、项目创建/读取、中文姓名与别名检索通过。
- 其他插件的 514 个既有文件 SHA256 保持不变。

旧版本备份：`artifacts/deepseek-defaults/deploy-20260912-231549/previous-plugin`。逐文件回执：同目录 `deployed-files.json`；其他插件部署前摘要：`other-plugins-before.json`。这些机器回执不加入版本库。

## 使用与边界

用户启动宿主后，新建连接默认 DeepSeek，输入自己的 API Key 后点“保存连接（配置与 Key）”。需要跨重启使用 Key 时勾选 Windows 加密保存；已有作品仍需明确绑定保存后的配置。

没有配置真实 DeepSeek Key，没有发送付费远端请求。协议字段用受控 HTTP/SSE 测试验证，未声称实际 DeepSeek 连通。本次未启动桌面宿主、未生成 ZIP 或执行 Windows CI；原有产品验收边界保持不变。
