# FakeSsh 架构概览

## 项目定位
AI 驱动的 SSH 蜜罐服务器。用户通过 SSH 连接后，与 AI 模拟的 Debian 12 终端交互。所有操作被记录用于安全分析。

## 技术栈
- **运行时**: .NET 10 (Console App, Generic Host)
- **SSH 协议**: FxSsh 1.3.0 (NuGet)
- **AI**: OpenAI 兼容 API (HttpClient 直接调用, SSE 流式)
- **日志**: JSONL 文件 + 内存 ConcurrentDictionary

## 核心架构

```
┌─────────────────────────────────────────────────┐
│                  Program.cs                      │
│         Host.CreateApplicationBuilder            │
│         注册 DI 服务, 启动 Host                    │
└──────────────────────┬──────────────────────────┘
                       │
        ┌──────────────▼──────────────┐
        │     SshServerHost            │
        │   (BackgroundService)        │
        │  监听端口, 接受 SSH 连接      │
        │  反射修改 Session 超时/获取IP  │
        └──────────────┬──────────────┘
                       │ 每个连接创建
        ┌──────────────▼──────────────┐
        │      ClientSession           │
        │  认证 → PTY → Shell          │
        │  命令分发:                    │
        │    本地命令 → 直接处理        │
        │    AI命令 → ChatService       │
        │  流式输出 + 安全防护          │
        └───┬──────────┬──────────┬───┘
            │          │          │
    ┌───────▼───┐ ┌────▼────┐ ┌──▼─────────────┐
    │ LineEditor │ │   VFS   │ │  ChatService   │
    │ 终端交互   │ │ 虚拟文件 │ │ AI 流式调用    │
    └───────────┘ └─────────┘ └──┬─────────────┘
                                  │
                          ┌───────▼────────┐
                          │ SystemPrompt   │
                          │ Builder        │
                          │ 构建系统提示词  │
                          └────────────────┘
```

## 数据流

1. **SSH 连接** → FxSsh `SshServer` 接受连接 → `OnConnectionAccepted`
2. **认证** → `UserauthService` → 检查用户名密码
3. **Shell 建立** → `ConnectionService` → PTY + Shell Channel
4. **用户输入** → `channel.DataReceived` → `LineEditor.ProcessBytes` (同步处理所有字节)
5. **命令提交** → `LineEditor` 回调 `OnCommandEntered`
6. **命令分类**:
   - `exit/logout/cd/pwd/clear/history` → 本地处理
   - 交互命令(vim/top等) → 直接返回 command not found
   - 其他 → `SendToAi` → `ChatService.StreamChatAsync`
7. **AI 响应** → SSE 流 → `StreamLineByLine` → `SendText` → `channel.SendData`
8. **日志** → `LogStore.AddEvent` → 内存 + JSONL 文件

## 关键设计决策

### 为什么用 FxSsh 而不是 SSH.NET?
SSH.NET 是 SSH **客户端**库。FxSsh 是目前 .NET 下唯一可用的 SSH **服务端**库。

### 为什么用反射修改 FxSsh?
FxSsh 的 `Session._timeout` 和 `Session._socket` 都是 private readonly，没有公开 API。只能通过反射访问：
- `_timeout`: 修改会话超时时间(默认硬编码 30s 太短)
- `_socket`: 获取客户端真实 IP 地址

### 为什么不用 OpenAI SDK?
直接用 HttpClient 调用更轻量，且需要支持各种 OpenAI 兼容 API (非官方端点)。SSE 流式解析也很简单。

### AI 输出安全边界
- 系统提示词包含防注入规则
- `MaxOutputLength` 限制单次输出总字节数 (默认 8192)
- 200 行上限
- 序号死循环检测 (正则匹配连续递增序号行，超过 15 行截断)
- `MaxTokens` 限制 AI 回复 token 数
