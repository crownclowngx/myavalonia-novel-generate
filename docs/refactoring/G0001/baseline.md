# G0001 实施前基线

日期：2026-09-12。Git 为 master，无历史提交，源文件原为未跟踪状态。

36 个源码、锁文件及文档文件摘要见 [baseline-files.json](baseline-files.json)；原文件快照保存在被 Git 忽略的
`artifacts/baseline/pre-g0001.zip`。仅清理本插件模板，不删除相邻插件或 Host。

基线环境为 Windows x64、.NET SDK 10.0.302、SDK/UI 3.4.0、Avalonia 12.1.0。
原始 locked restore、Debug 零警告构建及 4 项模板测试均通过。原测试仅证明演示逻辑，不能计入小说功能验收。

第一次阶段提交同时建立本项目 Git 根提交，包含已有产品/设计/实施文档与 G0001 修改；源码快照仍可用于比对原模板。
