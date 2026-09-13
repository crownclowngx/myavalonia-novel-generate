# G0038 实施方案

AnalysisStageSettings 保存五阶段模型、输出上限与思考强度；界面提供独立编辑，不修改创作连接的规划/正文/检查参数。TextModelRequest 的 ExecutionPreset 只覆盖生成参数，Configuration 仍保留真实已保存连接和凭据身份；DeepSeek 与 Codex 适配器统一读取 EffectivePreset。

每个节点保存 ExecutionConnection 与 ExecutionPreset。修订时范围不变的已完成节点保留其旧执行身份、提示与结果指纹；尚未完成节点使用新连接和阶段参数。冻结连接过期或认证端点变化仍阻止发送，不为复用缓存绕过授权校验。只有成功缓存路径无需远端认证。

旧运行缺少这些字段时使用原检查预设，输入指纹不额外增加空配置。报告及节点明细展示实际参数；混合配置不伪称由新参数重新生成。

专项见[配置与修订协议](stage-configuration-contract.md)。
