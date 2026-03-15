# FakeSsh - AI驱动的SSH蜜罐服务器

基于 .NET 10 构建的仿真SSH服务端，使用 OpenAI 兼容 API 模拟真实的 Debian 12 服务器环境。攻击者连接后将与 AI 驱动的虚拟终端交互，所有操作被详细记录。

## 功能特性

- SSH 服务端，支持密码认证（FxSsh 1.3.0）
- OpenAI 兼容 API 驱动的命令响应，逼真模拟 Debian 终端
- 虚拟文件系统，追踪用户操作（touch/mkdir/rm/重定向）
- 完整终端功能（方向键、历史记录、Ctrl 快捷键、Home/End/Delete）
- 流式输出，随机延迟模拟真实网络
- 输入限速，防止高速自动化探测
- AI 防御：防提示词注入、序号死循环检测、输出长度截断
- JSONL 格式日志记录，启动时自动加载历史会话

## 快速开始

### 前置要求

- [.NET 10 SDK](https://dot.net)
- OpenAI 兼容 API Key

### 配置

编辑 `src/FakeSsh/appsettings.json`：

```json
{
  "FakeSsh": {
    "SshPort": 2222,
    "Users": [
      { "Username": "root", "Password": "toor" }
    ],
    "OpenAi": {
      "ApiKey": "sk-your-api-key-here",
      "BaseUrl": "https://api.openai.com",
      "Model": "gpt-4o",
      "MaxTokens": 4096
    }
  }
}
```

### 运行

```bash
cd src/FakeSsh
dotnet run
```

### 连接

```bash
ssh root@localhost -p 2222
# 密码: toor
```

## 配置参数

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `SshPort` | SSH 监听端口 | 2222 |
| `Hostname` | 模拟的主机名 | debian-srv |
| `HostKeyPath` | RSA 主机密钥文件 | host_key.pem |
| `MaxInputRatePerSecond` | 每秒最大输入字符数 | 20 |
| `SessionTimeoutSeconds` | 会话空闲超时(秒) | 120 |
| `MaxOutputLength` | 单次命令最大输出字节数 | 8192 |
| `Users` | 允许登录的用户列表 | root:toor |
| `OpenAi.ApiKey` | API 密钥 | (必填) |
| `OpenAi.BaseUrl` | API 基础URL(支持兼容API) | https://api.openai.com |
| `OpenAi.Model` | 使用的模型 | gpt-4o |
| `OpenAi.MaxTokens` | AI 回复最大 token 数 | 4096 |

## 项目结构

```
src/FakeSsh/
├── Program.cs                  # 入口，配置 DI 和启动 Host
├── GlobalUsings.cs             # 全局 using 声明
├── Config/
│   └── AppConfig.cs            # 配置模型
├── Ssh/
│   ├── SshServerHost.cs        # SSH 服务端生命周期 (BackgroundService)
│   └── ClientSession.cs        # 客户端会话处理（认证、命令、AI交互）
├── Terminal/
│   └── LineEditor.cs           # 终端行编辑器（方向键、历史、快捷键）
├── FileSystem/
│   └── VirtualFileSystem.cs    # 虚拟文件系统
├── AI/
│   ├── ChatService.cs          # OpenAI API 流式调用
│   └── SystemPromptBuilder.cs  # 系统提示词构建
└── Logging/
    ├── SessionEvent.cs         # 日志事件模型
    └── LogStore.cs             # 日志存储（内存 + JSONL 文件）
```

## 日志

日志文件存储在 `bin/Debug/net10.0/logs/` 目录下，每个会话一个 `.jsonl` 文件。程序启动时自动加载历史日志。

## 安全说明

此项目仅用于安全研究和蜜罐部署，请勿用于非法用途。

## License

MIT
