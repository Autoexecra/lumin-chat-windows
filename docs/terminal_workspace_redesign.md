# LuminChatWin Terminal Workspace Redesign

## 1. 目标

这次重构聚焦四个直接的交互目标：

1. 把原来靠右的功能标签迁到左侧，形成稳定的工作入口。
2. 把“已配置会话”做成持久化入口，下次启动后仍可直接打开。
3. 把打开中的终端会话集中到中央上方标签区，支持快速切换。
4. 把 Agent 从“单次规划面板”改成更接近对话工作台的执行模式。

这不是单纯换皮。为了让右键共享开关真正生效，运行时和 API 暴露策略也一起调整了。

## 2. 新布局

终端控制窗口现在拆成两块主区域外加底部状态栏：

- 左侧导航区：`会话`、`连接`、`Agent`、`AI过程`、`共享`
- 中央视图区：打开中的终端标签页
- 底部状态栏：会话数量、当前会话状态、当前命令状态、窗口提示

设计意图：

- 左侧只放入口和配置，不占用中央操作面积。
- 中央只承载当前活动终端，避免配置表单和输出区域混在一起。
- 尽量去掉顶部空白和装饰，优先把垂直空间留给终端正文。
- 全局状态尽量下沉到状态栏，而不是占用终端上方的可视区域。

## 3. 配置会话持久化

### 3.1 存储模型

新增 `TerminalSessionProfile`，覆盖以下配置：

- 会话标题
- 会话类型：PowerShell / SSH / Telnet / Serial
- 连接参数
- `ssh_shared`
- `api_shared`
- 创建时间和最近使用时间

配置会话统一存储到：

- `terminal.profile_store_path`

默认路径：

- `~/.lumin-chat-win/terminal-profiles.json`

### 3.2 左侧会话标签

左侧 `会话` 标签展示所有已保存配置，并支持：

- 双击直接打开
- 右键重命名
- 右键删除
- 右键切换 `SSH 共享`
- 右键切换 `API 共享`
- 加载到连接表单后覆盖原配置

这样用户后续不需要再次手工填写主机、串口或工作目录。

## 4. 打开中的会话

中央区域改成顶部 `TabControl`，每个打开会话占一个标签：

- 标签头显示标题和类型
- 标签内容以单块终端正文为核心，采用深色终端样式
- 终端输入直接发生在正文区域，不再保留底部独立输入条
- 原来的“原始发送 / 关闭 / Ctrl+C”底部按钮不再放在会话正文内
- 会话 descriptor、共享状态、当前命令状态都移到窗口状态栏与轻量说明区
- 正文重新恢复为 ANSI 富文本渲染，串口、SSH、Telnet、PowerShell 输出中的颜色会保留

这版交互参考了 `yterminal` 的核心思路：

- 左边是入口和配置
- 中间是实际终端内容
- 输入和输出共用同一块终端视图，而不是割裂成两个区域
- 键盘事件直接转发到会话后端，支持回车、退格、方向键、翻页键和粘贴

## 5. 共享模型

### 5.1 API 共享

以前本地 HTTP API 会枚举所有打开会话。现在改为只暴露 `IsApiShared = true` 的会话。

影响：

- `GET /api/sessions` 只返回开启 API 共享的会话
- `history`、`current-output`、`exec_cmd`、`send_input`、`bridge/open` 也都先校验共享标记

这样左侧配置会话上的 `API 共享` 开关才有实际意义。

### 5.2 SSH 共享

`SSH 共享` 现在可用于所有终端会话：

- 如果配置会话启用 `SSH 共享`
- 且该会话类型支持桥接
- 打开会话后自动调用串口桥接启动逻辑

手工桥接入口仍保留在左侧 `共享` 标签中，但界面已简化成：

- 串口下拉列表
- 端口输入框
- 保存设置按钮
- 手工打开共享端口按钮

端口覆盖规则也改成按稳定的串口标识保存，例如 `COM12`，不再依赖每次启动都会变化的运行时 `sessionId`。

当前这条共享链路仍然是串口的原始 TCP relay，不是真正的 SSH 服务端。因此如果直接用 SSH 客户端连接，会在协议握手阶段失败；本次修改至少把默认监听地址改成了 `0.0.0.0`，并在 UI/状态里明确标注了这一点，避免继续把 raw relay 误认为可直接 SSH 登录的端口。

## 6. Agent 工作台

Agent 工作台拆成三个相邻标签：

1. `Agent` 标签：自动模式。模型选择、目标会话、需求输入，发送后会持续自动执行直到完成或需要人工补充信息。
2. `提示模式` 标签：只生成建议命令。手动执行后，会基于最新会话窗口内容自动继续下一轮规划。
3. `AI过程` 标签：实时显示 Agent 规划、工具调用、工具结果和执行过程。

新增行为：

- 模型下拉支持 `auto` 和具体 `levelN`
- 可选择目标终端会话
- `Agent` 标签默认自动执行，不再保留建议命令框和自动执行复选框
- `提示模式` 保留建议命令框，用于人工审核和手动执行
- 发给 LLM 的终端历史直接取当前会话窗口字符串，并截断到最近 10000 个字符
- 终端 Agent 只开放 `run_shell_command`、网页搜索/抓取、资料库、SSH 远程控制这些与串口场景直接相关的工具
- Agent 过程日志与配置区分离，避免压缩终端和输入区域

### Agent 输出协议

- 终端 Agent 统一按 `thinking / content / tool_calls` 三部分协议工作。
- `thinking` 通过模型的流式 reasoning 通道实时显示在 `AI过程` 标签。
- `content` 通过模型正文流式返回，并且必须始终是 JSON：`{"complete":bool,"analysis":string,"final_message":string}`。
- 如果任务未完成，模型不再把命令写进 JSON，而是通过 `tool_calls` 返回下一步动作。
- `run_shell_command` 在终端分支里不是执行本机 PowerShell，而是把 `command` 投递到当前选中的终端会话窗口中执行。
- 自动模式会直接执行 `run_shell_command`；提示模式会把这条 tool call 拦截成建议命令，等待用户确认。
- 综合设置新增“登录与探测”选项卡，可配置默认登录账号、默认密码、最大登录尝试次数和 prompt 正则。
- 终端执行前会先发送一次回车探测当前状态；若检测到 `login:` 或 `Password:`，会按配置最多尝试 3 次登录，登录成功后再下发命令。
- 真正执行命令前，还会再发送一次回车保存当前 shell prompt；随后发送命令和一个额外的回车，只有这个额外回车得到相同 prompt 响应，才视为命令结束。
- `run_shell_command` 默认超时为 120 秒；遇到下载、联网、硬件探测等可能长时间阻塞的任务时，模型应把 `timeout_seconds` 提高到最多 1800 秒。

这使 Agent 同时覆盖两种工作流：

- 自动执行流：用户给目标，Agent 规划并落命令
- 审核执行流：用户先看建议，再手工执行

## 7. 实现落点

本次关键改动文件：

- `LuminChatWin.App/TerminalControlWindow.xaml`
- `LuminChatWin.App/TerminalControlWindow.xaml.cs`
- `LuminChatWin.App/TextPromptWindow.xaml`
- `LuminChatWin.App/TextPromptWindow.xaml.cs`
- `LuminChatWin.Core/Models/TerminalModels.cs`
- `LuminChatWin.Core/Models/ConfigModels.cs`
- `LuminChatWin.Core/Services/TerminalProfileStore.cs`
- `LuminChatWin.Core/Services/TerminalSessionManager.cs`
- `LuminChatWin.Core/Services/TerminalApiServer.cs`
- `LuminChatWin.Core/Services/TerminalAgentService.cs`

## 8. 验证

本次改动已通过以下验证：

- `dotnet build c:\code\lumin-chat-windows\lumin-chat-win\LuminChatWin.sln`
- 全量 `LuminChatWin.Tests` 测试通过

新增覆盖点：

- `TerminalProfileStore` 的保存、重命名、共享开关和最近使用时间持久化
- `TerminalAgentService` 的工具白名单、终端 transcript 注入、`run_shell_command` 提示/自动双模式行为
- `OpenAiCompatibleChatClient` 的流式 reasoning/content 聚合与 tool call 拼装
