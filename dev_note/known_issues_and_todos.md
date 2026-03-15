# FakeSsh 已知问题与待办事项

## 已解决的问题

### 1. FxSsh 30秒超时 (已修复)
**问题**: FxSsh `Session._timeout` 硬编码为 30 秒，无公开 API。
**方案**: 通过反射修改 `_timeout` 字段。配置项 `SessionTimeoutSeconds` 控制。
**风险**: FxSsh 版本更新可能改变字段名。

### 2. 客户端 IP 为 unknown (已修复)
**问题**: FxSsh `Session` 不暴露 RemoteEndPoint。
**方案**: 反射获取 `Session._socket`，读取 `RemoteEndPoint`。

### 3. AI 序号死循环 (已修复)
**问题**: AI 有时产生无限递增序号列表 (1. 2. 3. ...)。
**方案**: 
- 系统提示词明确禁止
- `ClientSession` 中正则检测连续序号行，超过 15 行截断
- `MaxOutputLength` 字节数上限
- `MaxTokens` API 层 token 上限

### 4. AI 被提示词注入绕过 (已加固)
**问题**: 用户可通过命令注入指令操纵 AI。
**方案**: 系统提示词新增规则 16-18，明确列出常见攻击手法。

### 5. ESC 序列乱码 (已修复)
**问题**: 方向键持续按住时产生 `DD[DD[[[` 乱码。
**方案**: LineEditor 同步处理同一 chunk 内所有字节，不拆分 ESC 序列。

---

## 已知限制

### 1. 不支持交互式程序
vim/nano/top/htop/less/tmux 等 TUI 程序无法模拟，统一返回 command not found。

### 2. 管道和重定向部分支持
`echo "text" > file` 可以模拟文件创建，但复杂管道链 `cat file | grep x | sort` 是整条发给 AI 处理的。

### 3. 文件内容不持久
VFS 是内存级的，会话间不共享。每个新连接都是全新的文件系统。

### 4. 网络命令无法真实联网
`curl`、`wget`、`ping` 等命令的输出完全由 AI 编造，不会实际联网。

### 5. FxSsh 库限制
- 仅支持 RSA 主机密钥（不支持 Ed25519/ECDSA）
- 不支持 SFTP 子系统
- 不支持 SSH Agent forwarding
- 不支持端口转发

---

## 潜在改进方向

### 短期
- [ ] 支持 `Tab` 键自动补全（基于 VFS 路径）
- [ ] 支持 `Ctrl+R` 反向历史搜索
- [ ] 支持 `scp` / `sftp` 子系统（捕获上传的文件）
- [ ] 多会话间共享 VFS 状态

### 中期
- [ ] 基于 IP 的访问统计和频率限制
- [ ] 支持公钥认证（记录公钥指纹）
- [ ] 日志导出为 CSV/JSON 报告
- [ ] 加入 Webhook 通知（新连接时推送消息）

### 长期
- [ ] 替换 FxSsh 为自实现 SSH 协议栈（解除限制）
- [ ] 支持 SFTP/SCP 文件上传捕获
- [ ] 集群部署 + 集中日志收集
- [ ] AI 行为学习（根据历史攻击模式优化响应）
