# FakeSsh 开发指南：如何添加新功能

## 常见修改场景

### 场景 1: 添加新的本地命令（不需要 AI）

**文件**: `Ssh/ClientSession.cs` → `ProcessCommand` 方法

在 switch 语句中添加新 case：
```csharp
case "whoami":
    SendText($"{_username}\r\n");
    _commandLog.Add((command, _username));
    return;
```

### 场景 2: 添加新的交互命令黑名单

**文件**: `Ssh/ClientSession.cs` → `InteractiveCommands` 集合

```csharp
private static readonly HashSet<string> InteractiveCommands = new(StringComparer.OrdinalIgnoreCase)
{
    // 现有条目...
    "newcommand",  // 添加到这里
};
```

这些命令会直接返回 `command not found`，不会发给 AI。

### 场景 3: 修改模拟的系统信息

**文件**: `AI/SystemPromptBuilder.cs`

修改 `=== 系统信息 ===` 部分。注意同时更新 `FileSystem/VirtualFileSystem.cs` 中 `/proc/cpuinfo`、`/proc/meminfo` 等虚拟文件的内容，保持一致。

### 场景 4: 添加新的虚拟文件

**文件**: `FileSystem/VirtualFileSystem.cs` → 构造函数

```csharp
_files["/etc/newfile"] = "file content here\n";
```

### 场景 5: 修改 AI 防御规则

**文件**: `AI/SystemPromptBuilder.cs` → 绝对规则部分

添加新规则编号和内容。同时考虑是否需要在 `ClientSession.cs` 中添加对应的硬编码防护（提示词不是 100% 可靠的）。

### 场景 6: 添加新的配置项

1. `Config/AppConfig.cs`: 添加属性
2. `appsettings.json`: 添加默认值
3. 在需要使用的地方通过 `_config.NewProperty` 引用

### 场景 7: 添加新的日志事件类型

1. `Logging/SessionEvent.cs`: 在 `SessionEventType` 枚举中添加
2. `Logging/LogStore.cs`: 可选，添加便捷方法
3. 在触发点调用 `_logStore.AddEvent(new SessionEvent { ... })`

### 场景 8: 支持新的终端快捷键

**文件**: `Terminal/LineEditor.cs` → `ProcessNormal` 方法

Ctrl 键组合在此处理（0x01-0x1A 分别对应 Ctrl+A 到 Ctrl+Z）。

---

## FxSsh 使用注意事项

### 可用的公开 API
- `SshServer`: 创建、启动、停止
- `Session`: `ClientVersion`, `SessionId` (仅此两个公开属性)
- `UserauthService`: `Userauth` 事件
- `ConnectionService`: `PtyReceived`, `CommandOpened`, `WindowChange` 事件
- `SessionChannel`: `SendData`, `SendEof`, `SendClose`, `DataReceived`, `CloseReceived`
- `KeyGenerator.GenerateRsaKeyPem(bits)`

### 需要反射访问的
- `Session._timeout` (TimeSpan): 会话超时
- `Session._socket` (Socket): 获取客户端 IP

### FxSsh 的限制
- 仅支持 RSA 主机密钥
- 不支持 SFTP/SCP 子系统
- Session 超时和 Socket 都是 private readonly
- 错误事件 `ExceptionRasied`（注意拼写是库的原始拼写，不是 typo）

---

## 代码风格约定
- 异步方法后缀 `Async`
- 私有字段前缀 `_`
- 日志使用结构化参数 `{Name}` 而非字符串拼接
- SSH 发送文本使用 `\r\n` 换行（终端协议要求）
- AI 接收和存储使用 `\n` 换行
