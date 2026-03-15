# LuminChatWin Terminal Workspace Redesign

## 1. 目标

这次重构聚焦四个直接的交互目标：

1. 把原来靠右的功能标签迁到左侧，形成稳定的工作入口。
2. 把“已配置会话”做成持久化入口，下次启动后仍可直接打开。
3. 把打开中的终端会话集中到中央上方标签区，支持快速切换。
4. 把 Agent 从“单次规划面板”改成更接近对话工作台的执行模式。

这不是单纯换皮。为了让右键共享开关真正生效，运行时和 API 暴露策略也一起调整了。

## 2. 新布局

终端控制窗口现在拆成两块主区域：

- 左侧导航区：`会话`、`连接`、`Agent`、`共享`
- 中央视图区：打开中的终端标签页

设计意图：

- 左侧只放入口和配置，不占用中央操作面积。
- 中央只承载当前活动终端，避免配置表单和输出区域混在一起。
- 顶部状态卡只展示全局运行态，如 API 地址、已打开会话数量。

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
- 标签内容显示 descriptor、最近活动、当前命令状态
- 下方终端区采用深色终端样式
- 输入条嵌入终端底部，不再使用旧版全局独立输入框

这版交互参考了 `yterminal` 的核心思路：

- 左边是入口和配置
- 中间是实际终端内容
- 终端输入贴近输出区域，而不是割裂到窗口底部的另一个模块

## 5. 共享模型

### 5.1 API 共享

以前本地 HTTP API 会枚举所有打开会话。现在改为只暴露 `IsApiShared = true` 的会话。

影响：

- `GET /api/sessions` 只返回开启 API 共享的会话
- `history`、`current-output`、`exec_cmd`、`send_input`、`bridge/open` 也都先校验共享标记

这样左侧配置会话上的 `API 共享` 开关才有实际意义。

### 5.2 SSH 共享

`SSH 共享` 目前主要针对串口会话：

- 如果配置会话启用 `SSH 共享`
- 且该会话类型支持桥接
- 打开会话后自动调用串口桥接启动逻辑

手工桥接入口仍保留在左侧 `共享` 标签中。

## 6. Agent 工作台

Agent 标签调整成两段式结构：

1. 上半部分：执行过程与对话记录
2. 下半部分：用户需求输入与建议命令区域

新增行为：

- 模型下拉支持 `auto` 和具体 `levelN`
- 可选择目标终端会话
- 可勾选“Agent 直接执行建议命令”
- 若未勾选自动执行，则建议命令保留在底部，由用户手工点击执行

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
