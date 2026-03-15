# FakeSsh 配置与部署指南

## 开发环境

### 前置要求
- .NET 10 SDK
- OpenAI 兼容 API Key

### 本地运行
```bash
cd src/FakeSsh
# 编辑 appsettings.json 设置 API Key
dotnet run
```

### 调试
项目使用 `FakeSsh: Debug` 日志级别。关键日志：
- SSH 连接/断开
- 认证成功/失败
- 每条命令及 AI 响应的 chunk 数和长度
- 序号死循环截断警告
- 反射操作成功/失败

---

## 配置文件说明 (appsettings.json)

```jsonc
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "FakeSsh": "Debug"          // 蜜罐组件详细日志
    }
  },
  "FakeSsh": {
    "SshPort": 2222,              // SSH 监听端口
    "Hostname": "debian-srv",     // 模拟主机名(影响提示符和系统信息)
    "HostKeyPath": "host_key.pem",// RSA 主机密钥路径(不存在则自动生成)
    "MaxInputRatePerSecond": 20,  // 每秒最大输入字节数(防自动化)
    "SessionTimeoutSeconds": 120, // 空闲超时秒数(FxSsh默认30s太短)
    "MaxOutputLength": 8192,      // 单次命令最大输出字节(防AI死循环)
    "Users": [                    // 允许登录的用户
      { "Username": "root", "Password": "toor" },
      { "Username": "admin", "Password": "admin123" }
    ],
    "OpenAi": {
      "ApiKey": "sk-xxx",         // API 密钥
      "BaseUrl": "https://api.openai.com",  // 支持任何兼容端点
      "Model": "gpt-4o",         // 模型名称
      "MaxTokens": 4096          // AI 单次回复 token 上限
    }
  }
}
```

---

## 生产部署

### Linux systemd 服务
```ini
[Unit]
Description=FakeSsh Honeypot
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/fakessh
ExecStart=/opt/fakessh/FakeSsh
Restart=always
RestartSec=5
User=fakessh
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
```

### Docker (示例)
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FakeSsh/ .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 2222
ENTRYPOINT ["./FakeSsh"]
```

### 注意事项
- `host_key.pem` 重新生成会导致已知客户端报 host key 变更警告
- 日志存储在 `logs/` 目录下，注意磁盘空间
- API Key 建议通过环境变量注入: `FakeSsh__OpenAi__ApiKey=sk-xxx`
- 建议将 SSH 端口设为 22 并用 iptables 或 firewall 规则将真实 SSH 移到其他端口
