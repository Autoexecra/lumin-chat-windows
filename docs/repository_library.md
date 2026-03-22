# 资料库设计

本文件描述 Windows 终端分支中的资料库实现，相关代码集中在以下位置：

- `LuminChatWin.Core/Services/ToolExecutor.cs`
- `LuminChatWin.Core/Services/TerminalAgentService.cs`
- `LuminChatWin.App/SettingsWindow.xaml.cs`

## 目标

- 资料库优先服务于终端 Agent。
- 远端资料通过 SSH/SFTP 直接访问。
- 已读取的资料会写入本地缓存目录，减少重复网络读取。
- 单次 Agent 会话内使用内存缓存，结束后自动释放，不做长期常驻。

## 流程

1. `list_knowledge_documents` 首次调用时，同时扫描本地缓存目录和远端资料目录。
2. 远端和本地按相对路径去重，生成一份合并后的资料索引。
3. `read_knowledge_document` 优先命中本次会话内存缓存，其次读取本地缓存，再回退到远端读取。
4. 远端读取成功后，资料内容会写入本地缓存目录。
5. 当前 Agent 请求结束后，`ToolExecutor` 实例释放，会话级内存缓存一并释放。

## 配置

- 远端根目录：`knowledge_base.root_dir`
- 本地缓存目录：`knowledge_base.local_cache_dir`
- 调试界面名称统一显示为“资料库”，配置文件字段仍兼容 `knowledge_base`

## 调试

- 综合设置里提供“测试资料库连接”按钮，用于验证 SSH 连通性和目录可读性。
- 如果启用 LLM 调试日志，资料相关的提示词和模型返回也会一起落盘，方便排查模型是否真的先查资料后决策。