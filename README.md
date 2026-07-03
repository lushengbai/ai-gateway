# AI API Gateway — 本地代理工具（反向代理 / 前向代理）

一个基于 **.NET 8 WPF** 的本地代理，用于让**第三方 API 可直接访问**并观测流量。支持两种模式：

| 模式 | 客户端如何配置 | 用途 |
|---|---|---|
| **Reverse Proxy（默认）** | `base_url = http://127.0.0.1:{端口}[/前缀]` | 按路由规则把请求**改写/转发**到你配置的第三方 API 真实地址；无需证书；支持流式 SSE |
| **Forward Proxy** | `HTTPS_PROXY = http://127.0.0.1:{端口}` | 原始的 Titanium 显式 HTTP(S) 代理，识别/解密已知 AI 主机并记录 |

两种模式都支持**上游 HTTP 代理**（把全部转发流量再经一层代理出去），UI 采用 **MVVM（CommunityToolkit.Mvvm）**。

---

## 📖 概念说明：正向代理 vs 反向代理 vs Filter

### 正向代理 (Forward Proxy)
**代理站在「客户端」这一侧，替客户端出去访问。** 客户端知道自己在用代理。

- 配置方式：设环境变量 `HTTPS_PROXY=http://127.0.0.1:8080`（或系统/软件里的「代理服务器」）。
- 客户端请求的目标地址**不变**（还是 `https://api.openai.com`），只是先交给代理转发。
- 典型用途：翻墙、抓包、给一批 App 统一出口。
- 本工具里要看到 HTTPS 的完整 URL/状态码需解密，所以要装根证书。

```
客户端 ──(我要访问 api.openai.com，请帮我转)──►  正向代理  ──►  api.openai.com
   ↑ 客户端主动指定用代理，目标地址它自己知道
```

### 反向代理 (Reverse Proxy)
**代理站在「服务器」这一侧，伪装成目标服务器。** 客户端**不知道**背后是谁，以为自己直连 API。

- 配置方式：把客户端 `base_url` 改成 `http://127.0.0.1:8080/openai`。
- 客户端以为 `127.0.0.1:8080` 就是 API 本身；代理内部按路由改写并转发到真正的第三方 API。
- 典型用途：换域名、把第三方 API「包装」成本地可直接访问的地址。
- **不用装证书** —— 这就是「第三方 API 直接访问」的关键。

```
客户端 ──(我要访问 127.0.0.1:8080/openai)──►  反向代理  ──改写──►  真实第三方 API
   ↑ 客户端以为这就是终点，不知道被转发了
```

**一句话区分：**

| | 代表谁 | 客户端怎么配 | 客户端知不知情 |
|---|---|---|---|
| 正向代理 | 替客户端出去 | 设 `HTTPS_PROXY` | 知道自己在用代理 |
| 反向代理 | 假装是服务器 | 改 `base_url` | 不知道，以为直连 |

> 把第三方 API 变成本地可直接访问，用的是**反向代理**。

### Filter（日志过滤器）
界面顶部的下拉框，**只影响下方日志表格的显示**，不影响转发行为，可随时切换、无需停代理。

- 选 `All`：显示所有请求日志。
- 选某个名字（如 `OpenAI` / `Claude` / 你自定义的路由名）：只显示该路由/提供商的请求。
- 下拉选项来自你配置的路由名（反向模式）和目标主机名（正向模式）。

> 注意与 `OnlyLogAiRequests`（复选框，仅正向模式）区分：后者决定**记不记录**某条请求，Filter 决定**显示哪些**——两回事。

---

## ✨ 你要的三点

1. **自定义代理的 API** —— 在界面「Routes」表中增删/编辑路由：`PathPrefix → TargetBaseUrl`（任意第三方 API）。
2. **上游 HTTP 代理主机/端口** —— 勾选「Route all traffic via upstream HTTP proxy」，填写 Host / Port（可选 User / Pass），全部转发流量走此上游。
3. **第三方 API 直接访问** —— 反向代理模式下，客户端只把 `base_url` 指向本工具即可，无需改动其它代码，也无需装证书。

---

## 🔁 反向代理是怎么工作的

```
客户端 (base_url = http://127.0.0.1:8080/openai)
        │  POST /openai/v1/chat/completions
        ▼
   AI Gateway   ── 规则: 前缀 /openai → https://api.openai.com
        │        改写为 /v1/chat/completions
        │  （可选）经上游 HTTP 代理 host:port 出去
        ▼
   https://api.openai.com/v1/chat/completions
```

- **最长前缀匹配**：多个路由时选最长匹配；`PathPrefix` 留空或 `/` 表示**默认兜底路由**（此时 `base_url = http://127.0.0.1:8080` 即可）。
- 请求方法、Header（含 `Authorization`）、Body 原样转发；响应**流式**回传（适配 OpenAI / Claude 的 SSE）。
- 未命中任何路由返回 `502` 并记录日志。

### 示例：把一个第三方 OpenAI 兼容 API 变成可直接访问

路由：`Name=MyLLM`，`PathPrefix=/llm`，`TargetBaseUrl=https://my-3rd-api.com/v1`
客户端：`OPENAI_BASE_URL=http://127.0.0.1:8080/llm`
→ 请求 `/llm/chat/completions` 实际到 `https://my-3rd-api.com/v1/chat/completions`。

---

## 🗂️ 项目结构

```
AiGateway.sln
src/AiGateway/
├── AiGateway.csproj / app.manifest
├── App.xaml(.cs) · MainWindow.xaml(.cs)
├── Config/AppConfig.cs            # 端口/模式/上游代理/路由/前向目标，JSON 持久化
├── Core/
│   ├── IProxyBackend.cs           # 统一的 Start/Stop/IsRunning/Message
│   ├── ReverseProxyService.cs     # ★ HttpListener 反向代理：改写+转发+流式+上游
│   ├── ForwardProxyService.cs     # Titanium 前向代理（+上游代理）
│   ├── RequestHandler.cs          # 前向模式的请求/响应/隧道事件
│   └── ApiRouter.cs               # 反向:路径前缀匹配 / 前向:主机匹配
├── Logging/LogEntry.cs · LogService.cs
└── UI/MainViewModel.cs · Converters.cs
test/SmokeTest/                    # 反向代理集成冒烟测试（echo 上游）
README.md
```

---

## 🚀 运行与调试

**前置**：Windows 10/11 · .NET 8 SDK（已验证 `8.0.303`）

```bash
# 编译
dotnet build AiGateway.sln -c Debug

# 运行
dotnet run --project src/AiGateway/AiGateway.csproj
```

**使用（反向代理）**
1. 在「Settings → Routes」配置好路由（默认已有 `/openai`、`/claude` 两条）。
2. 如需上游代理，勾选并填写 Host/Port。
3. 选 Mode = `ReverseProxy`，输入端口，点 **Start**。
4. 客户端把 `base_url` 指向 `http://127.0.0.1:{端口}/{前缀}`，正常发请求，日志实时显示。

**冒烟测试**（验证改写/转发/流式/日志）
```bash
cd test/SmokeTest && dotnet run -c Debug
# 期望输出结尾：ALL PASSED
```

**调试**：VS / VS Code 打开 `AiGateway.sln`，断点放在 `Core/ReverseProxyService.HandleAsync`。

---

## ⚙️ 配置文件（可执行文件旁 `appconfig.json`）

```json
{
  "Port": 8080,
  "Mode": "ReverseProxy",
  "UpstreamProxy": {
    "Enabled": false,
    "Host": "",
    "Port": 7890,
    "Username": "",
    "Password": ""
  },
  "Routes": [
    { "Name": "OpenAI", "PathPrefix": "/openai", "TargetBaseUrl": "https://api.openai.com", "Enabled": true },
    { "Name": "Claude", "PathPrefix": "/claude", "TargetBaseUrl": "https://api.anthropic.com", "Enabled": true }
  ],
  "OnlyLogAiRequests": true,
  "TrustRootCertificate": true,
  "Targets": [
    { "Name": "OpenAI", "HostSuffix": "api.openai.com",    "Decrypt": true },
    { "Name": "Claude", "HostSuffix": "api.anthropic.com", "Decrypt": true }
  ]
}
```

- `Routes` / `UpstreamProxy` 用于 **反向代理**。
- `OnlyLogAiRequests` / `TrustRootCertificate` / `Targets` 用于 **前向代理**。

---

## 📋 日志字段

时间、Provider（路由/主机名，可过滤）、Method、状态码（颜色区分）、耗时(ms)、请求/响应大小、目标 URL。

## ⚠️ 约束

- 仅 HTTP，不支持 SOCKS5。
- 反向代理直连第三方 API，无需 MITM 证书；前向代理仅对已知 AI 主机做解密。
- 稳定优先、结构清晰、易扩展（增路由/目标即可支持更多 API）。

---

## ⚡ 反向代理性能优化

已应用以下优化（`Core/ReverseProxyService.cs`、`Logging/LogService.cs`）：

1. **日志异步投递**：`LogService` 不再用同步 `dispatcher.Invoke`，改为 `BeginInvoke`，代理线程不再等待 UI 线程，降低每请求延迟。
2. **按内容类型决定 flush**：仅对 `text/event-stream` 或未知长度的流式响应**逐块 flush**；普通响应交给底层缓冲、结束时统一 flush，提升吞吐。
3. **智能 framing**：上游返回 `Content-Length` 时用定长帧（不分块）；只有流式/未知长度才用 chunked，减少分帧开销。
4. **`SocketsHttpHandler` 连接池调优**：`PooledConnectionLifetime=2min`、`IdleTimeout=90s`、`MaxConnectionsPerServer=256`、`ConnectTimeout=15s`，同一 API 高频请求连接复用更好。
5. **`ArrayPool` 64KB 缓冲**：转发缓冲区改为池化 64KB，减少 GC 压力与循环次数。
6. **HTTP/2 协商**：对上游优先 HTTP/2（多路复用），不支持时自动降级 HTTP/1.1（`RequestVersionOrLower`）。
7. **日志 UI 批量刷新**：新日志缓冲后由 `DispatcherTimer` 以 ~10Hz 批量插入，高吞吐时避免每请求触发一次 UI 刷新。

> 集成测试（`test/SmokeTest`）覆盖定长响应与流式（SSE）两条路径，均通过。
