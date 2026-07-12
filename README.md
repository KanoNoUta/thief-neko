# Thief Neko

Thief Neko 将 Catpaw 中的 `glm-5.2` 接入 Claude Code、Codex 和其他兼容 OpenAI API 的客户端。

它同时提供：

- Anthropic Messages：`/v1/messages`
- OpenAI Chat Completions：`/v1/chat/completions`
- OpenAI Responses：`/v1/responses`
- Claude Code 与 Codex 工具调用适配
- Catpaw Token 自动刷新与 401 无感重试
- 剩余额度低于 4 时自动申请额外额度
- 请求次数、Token、内存和 Catpaw 剩余额度统计
- Windows 图形控制器与 Linux 无界面服务

> 本项目仅用于你自己的 Catpaw 账号和额度，与 Catpaw、美团、Anthropic、OpenAI、CCSwitch 无官方关联。

## 下载

从 [GitHub Releases](https://github.com/KanoNoUta/thief-neko/releases) 下载：

| 平台 | 文件 |
| --- | --- |
| Windows x64 | `Thief-Neko-v0.2.4-win-x64.zip` |
| Linux x64 | `Thief-Neko-v0.2.4-linux-x64.tar.gz` |

## Windows

要求：Windows 10/11 x64、Node.js 24+。

1. 完整解压 Windows 压缩包。
2. 运行 `ThiefNeko.exe`。
3. 使用窗口内二维码、手机验证码或系统浏览器登录 Catpaw，也可以手动填写 Token。
4. 保存配置并启动网关。

`ThiefNeko.exe` 已包含 .NET 运行时，但网关仍需要 Node.js 24+。

## Linux

要求：Linux x64、Node.js 24+。

```bash
tar -xzf Thief-Neko-v0.2.4-linux-x64.tar.gz
cd Thief-Neko-v0.2.4-linux-x64
export CATPAW_PHONE="your-mobile-number"
export CATPAW_SESSION_PATH="$HOME/.local/share/thief-neko/session.enc"
export CATPAW_SESSION_KEY_PATH="$HOME/.config/thief-neko/session.key"
node src/linuxLogin.js send
```

收到验证码后完成登录：

```bash
export CATPAW_SMS_CODE="your-sms-code"
node src/linuxLogin.js complete
```

然后设置其余环境变量并启动：

```bash
export CATPAW_BASE_URL="https://catpaw.meituan.com"
export CATPAW_UPSTREAM_URL="https://catpaw.meituan.com/api/gpt/openai/stream"
export CATPAW_TENANT="5282fa6645"
export CATPAW_MODEL="glm-5.2"
export HOST="127.0.0.1"
export PORT="3000"
node src/server.js
```

远程部署时将 `HOST` 改为 `0.0.0.0`，并必须设置独立入口密钥：

```bash
export CATAPI_API_KEY="replace-with-a-long-random-secret"
```

建议使用 systemd 管理进程，并通过防火墙或反向代理限制访问。

## CCSwitch

### Codex Desktop

1. 打开 CCSwitch，进入 `Providers`，选择 `Codex`。
2. 点击新增自定义 Provider，名称填写 `Thief Neko GLM-5.2`。
3. API Key 本地直连填写 `local-gateway`；经过 New API 时填写 New API 生成的 Token。
4. 将下面内容粘贴到 Codex 配置中：

```toml
model_provider = "custom"
model = "glm-5.2"
model_reasoning_effort = "high"
disable_response_storage = true

[model_providers.custom]
name = "Thief Neko"
base_url = "http://127.0.0.1:3000/v1"
wire_api = "responses"
requires_openai_auth = true
```

5. 模型列表或模型映射中添加 `glm-5.2`，显示名称也填写 `GLM-5.2`。
6. 保存并切换到该 Provider，然后完全退出并重新启动 Codex Desktop。

服务端经过 New API 时，只需修改：

```toml
[model_providers.custom]
name = "Thief Neko Server"
base_url = "http://your-server:3000/v1"
wire_api = "responses"
requires_openai_auth = true
```

这里的端口是 New API 对外端口，不是只监听服务器本机的 Thief Neko 内部端口。

### Claude Code

1. 在 CCSwitch 中选择 `Claude` 或 `Claude Desktop` Provider。
2. 新建 Provider，名称填写 `Thief Neko GLM-5.2`。
3. 配置以下环境变量：

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://127.0.0.1:3000",
    "ANTHROPIC_API_KEY": "local-gateway"
  }
}
```

4. 保存并设为当前 Provider，然后完全退出并重新启动 Claude Code Desktop。

经过 New API 时，将 `ANTHROPIC_BASE_URL` 改为 New API 地址，并把 `ANTHROPIC_API_KEY` 改为 New API Token。请确认使用的 New API 版本支持 Anthropic Messages 转发；Codex 则优先使用 Responses 配置。

## New API

创建 OpenAI 兼容渠道：

```text
Base URL: http://thief-neko-host:3000
API Key:  CATAPI_API_KEY 的值
Model:    glm-5.2
```

网关提供 New API 余额查询兼容接口，可将 Catpaw 剩余请求次数同步到渠道余额。完整说明见 [docs/NEW-API.md](docs/NEW-API.md)。

## 手动获取 Token

已登录 Catpaw 的 Windows 电脑可以执行：

```powershell
$state = node .\src\catpawState.js | ConvertFrom-Json
$state.token | Set-Clipboard
```

也可以在 Catpaw 开发者工具的 Network 面板中筛选 `/api/user/limit`，复制请求头 `Catpaw-Auth` 的原始值。不要添加 `Bearer `，也不要把 Token 发到 Issue、日志或 Git 仓库。

## 开发

```powershell
npm test
powershell -ExecutionPolicy Bypass -File .\controller\release.ps1
```

健康检查：

```bash
curl http://127.0.0.1:3000/health
curl http://127.0.0.1:3000/v1/models
curl http://127.0.0.1:3000/admin/status
```

## License

[MIT](LICENSE)
