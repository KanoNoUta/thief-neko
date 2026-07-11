# New API 接入

Thief Neko 原生支持 Anthropic Messages、OpenAI Chat Completions 和 OpenAI Responses。Codex 应使用 `/v1/responses`，普通 OpenAI 客户端使用 `/v1/chat/completions`。

## 服务端配置

```env
HOST=0.0.0.0
PORT=3000
CATAPI_API_KEY=replace-with-a-long-random-secret
CATPAW_MODEL=glm-5.2
```

监听非本机地址时，`CATAPI_API_KEY` 是必填项。它是 New API 到 Thief Neko 的入口密钥，不是 Catpaw Token。

## 渠道配置

在 New API 中创建 OpenAI 兼容渠道：

```text
Base URL: http://thief-neko-host:3000
API Key:  CATAPI_API_KEY 的值
Model:    glm-5.2
```

支持的接口：

| 协议 | 路径 |
| --- | --- |
| Anthropic Messages | `/v1/messages` |
| OpenAI Chat Completions | `/v1/chat/completions` |
| OpenAI Responses | `/v1/responses` |
| 模型列表 | `/v1/models` |
| 渠道余额 | `/v1/dashboard/billing/subscription`、`/v1/dashboard/billing/usage` |

余额接口将 Catpaw 总次数和已用次数映射为 New API 渠道余额。New API 界面可能按站点货币汇率显示，因此货币数字不等同于真实请求次数。

## GLM-5.2 价格

按智谱官方价格配置：

| 项目 | 价格 | New API 倍率 |
| --- | ---: | ---: |
| 输入 | 8 元 / 1M Token | `0.5714285714` |
| 输出 | 28 元 / 1M Token | `3.5` |
| 缓存命中 | 2 元 / 1M Token | `0.25` |

## 验证

```bash
curl -H "Authorization: Bearer replace-with-a-long-random-secret" \
  http://thief-neko-host:3000/v1/models
```

Responses 请求：

```bash
curl http://thief-neko-host:3000/v1/responses \
  -H "Authorization: Bearer replace-with-a-long-random-secret" \
  -H "Content-Type: application/json" \
  -d '{"model":"glm-5.2","input":"reply OK"}'
```

Thief Neko 支持 Codex namespace tools、自定义工具、流式工具参数和 `previous_response_id`。托管搜索、Files、Vector Stores、Realtime 与 WebSocket Responses 不在支持范围内。
