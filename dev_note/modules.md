# FakeSsh 各模块详细说明

## 1. Program.cs (入口)

**职责**: 配置 DI 容器并启动 Generic Host。

**DI 注册**:
- `AppConfig` → 从 `appsettings.json` 的 `FakeSsh` 节绑定
- `LogStore` → 单例，管理会话日志
- `ChatService` → 单例，AI API 客户端
- `SshServerHost` → HostedService，SSH 服务端

**修改指南**: 如需添加新服务，在 `builder.Services` 中注册即可。

---

## 2. Config/AppConfig.cs (配置)

**类结构**:
```
AppConfig
├── SshPort: int = 2222
├── Hostname: string = "debian-srv"
├── HostKeyPath: string = "host_key.pem"
├── MaxInputRatePerSecond: int = 20        # 输入限速
├── SessionTimeoutSeconds: int = 120       # 会话超时
├── MaxOutputLength: int = 8192            # 单命令输出上限(字节)
├── Users: List<UserCredential>            # 登录凭据
└── OpenAi: OpenAiConfig
    ├── ApiKey: string
    ├── BaseUrl: string
    ├── Model: string
    └── MaxTokens: int = 4096             # AI回复token上限
```

**修改指南**: 添加新配置项时，同时更新此类和 `appsettings.json`。

---

## 3. Ssh/SshServerHost.cs (SSH 服务端)

**职责**: BackgroundService，管理 FxSsh SshServer 生命周期。

**关键逻辑**:
- `ExecuteAsync`: 创建 SshServer，加载/生成 RSA 4096 主机密钥，设置 Banner
- `OnConnectionAccepted`: 每个新连接：
  1. 反射修改 `Session._timeout` (默认 30s → 配置值)
  2. 反射获取 `Session._socket.RemoteEndPoint` 提取客户端 IP
  3. 创建 `ClientSession` 实例
- `ExceptionRasied`: 错误处理，常见网络错误降级为 Debug 日志

**Banner**: `SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3` (模拟 Debian 12)

**修改指南**: 如需支持更多认证方式(如公钥)，修改 `ClientSession.OnAuth`。

---

## 4. Ssh/ClientSession.cs (会话处理) ⭐ 核心文件

**职责**: 一个 SSH 连接的完整生命周期管理。

**生命周期**:
1. 构造 → 注册事件 → 等待认证
2. 认证成功 → PTY 协商 → Shell 打开
3. 创建 LineEditor + VirtualFileSystem
4. 发送 MOTD → 进入命令循环
5. 断开连接 → 清理

**命令处理流程** (`ProcessCommand`):
```
命令 → 解析首词 → sudo剥离 →
  交互命令? → "command not found"
  cd/pwd/exit/clear/history? → 本地处理
  其他 → VFS副作用 → SendToAi
```

**AI 输出安全防护** (在 `StreamLineByLine` 和 `SendToAi` 中):
- `_currentOutputBytes`: 追踪已输出字节数，超过 `MaxOutputLength` 截断
- `_currentOutputLines`: 追踪已输出行数，超过 200 行截断
- `_consecutiveNumberedLines`: 检测连续序号行，超过 15 行截断
- `NumberedLinePattern`: 正则 `^\s*\d{1,6}[\.\)\]:：、]\s` 匹配序号格式

**SlowCommands**: apt/wget/git 等命令用 30-200ms 行间延迟
**NormalCommands**: 5-50ms 行间延迟

**修改指南**:
- 添加新的本地命令: 在 `ProcessCommand` 的 switch 中添加 case
- 调整输出安全阈值: 修改 `MaxOutputLength`、200行上限、15行序号阈值
- 添加新的交互命令黑名单: 在 `InteractiveCommands` 集合中添加

---

## 5. Terminal/LineEditor.cs (行编辑器)

**职责**: 处理原始终端字节流，提供行编辑功能。

**状态机**: Normal → Escape → CSI (处理 ESC 序列)

**支持的操作**:
- 方向键: 左右移动光标、上下翻历史
- Home/End: 行首/行尾
- Delete: 删除光标后字符
- Backspace: 删除光标前字符
- Ctrl+A/E: 行首/行尾
- Ctrl+C: 取消当前行
- Ctrl+D: 光标后删除（空行时忽略）
- Ctrl+K: 删除光标到行尾
- Ctrl+U: 删除光标到行首
- Ctrl+W: 删除前一个词
- Ctrl+L: 清屏
- Enter: 提交命令

**关键设计**: 所有字节在一个 data chunk 内同步处理（避免 ESC 序列被拆分导致乱码）。速率限制按 chunk 而非按字节。

**修改指南**: 添加新快捷键在 `ProcessNormal` 方法中添加 case。

---

## 6. FileSystem/VirtualFileSystem.cs (虚拟文件系统)

**职责**: 维护内存中的虚拟 Debian 目录树。

**功能**:
- 预置 Debian 12 标准目录结构
- 虚拟文件: `/proc/cpuinfo`, `/proc/meminfo`, `/etc/hostname`, `/etc/os-release` 等
- `ApplyCommandSideEffects`: 解析 touch/mkdir/rm/echo重定向，更新 VFS
- `ResolvePath`: 路径解析（支持相对路径、..、~）
- `GetChangesSummary` / `GetUserFilesInfo`: 为 AI 提供文件系统上下文

**修改指南**: 添加新的虚拟文件在构造函数中添加。添加新的命令副作用在 `ApplyCommandSideEffects` 中添加。

---

## 7. AI/ChatService.cs (AI 调用)

**职责**: 调用 OpenAI 兼容 API，流式返回结果。

**API 调用**:
- 端点: `POST /v1/chat/completions`
- 流式: SSE (Server-Sent Events)
- 超时: HttpClient 3 分钟

**方法**:
- `StreamChatAsync`: 流式返回 `IAsyncEnumerable<string>`
- `CompleteChatAsync`: 非流式（内部仍流式，拼接结果）

**对话上下文**: 保留最近 20 条消息 (`conversationHistory.TakeLast(20)`)

**修改指南**: 如需切换到其他 AI 提供商，只需修改 BaseUrl 和可能的请求格式。

---

## 8. AI/SystemPromptBuilder.cs (系统提示词)

**职责**: 构建详细的系统提示词，指导 AI 模拟 Debian 终端。

**提示词结构**:
1. 绝对规则 (1-10): 基本行为约束
2. 输出长度限制 (11-15): 防止超长输出和序号死循环
3. 防提示词注入 (16-18): 防止用户通过命令操纵 AI
4. 系统信息: 硬件配置、网络、Zeit
5. 会话信息: 用户、CWD、终端
6. 软件包列表: 已安装/未安装
7. VFS 状态和命令历史

**硬件配置** (故意夸张):
- CPU: AMD EPYC 9754 128核
- GPU: 8x NVIDIA H200
- RAM: 10TB
- Disk: 10PB NVMe

**修改指南**:
- 调整模拟的系统配置: 修改"系统信息"部分
- 添加/移除已安装软件: 修改软件包列表
- 加强防注入: 在规则 16-18 中添加新的攻击模式

---

## 9. Logging/LogStore.cs + SessionEvent.cs (日志)

**职责**: 会话和事件的存储管理。

**存储**:
- 内存: `ConcurrentDictionary<string, SessionInfo>` (按 sessionId)
- 磁盘: `logs/{sessionId}.jsonl` (追加写入)

**事件类型**: Connected, AuthAttempt, AuthSuccess, AuthFailed, Command, AiResponse, Disconnected

**启动加载**: 构造函数中调用 `LoadHistoricalSessions()`，扫描 `logs/*.jsonl` 重建内存数据。

**修改指南**: 添加新事件类型在 `SessionEventType` 枚举中添加，然后在需要的地方调用 `AddEvent`。
