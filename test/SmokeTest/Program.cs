using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AiGateway.Config;
using AiGateway.Core;
using AiGateway.Logging;

// Integration smoke test for ReverseProxyService:
//  fake upstream API (echo) <-- ReverseProxyService <-- test client
// Verifies path rewrite (prefix strip), method/body pass-through, and response streaming.

const int upstreamPort = 18091; // stands in for the "third-party API"
const int gatewayPort  = 18092; // the tool's reverse proxy

// 1) Fake third-party API that echoes what it received.
var upstream = new HttpListener();
upstream.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
upstream.Start();
var upstreamLoop = Task.Run(async () =>
{
    while (upstream.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await upstream.GetContextAsync(); }
        catch { break; }

        string body;
        using (var r = new StreamReader(ctx.Request.InputStream)) body = await r.ReadToEndAsync();

        // Streaming (SSE-style, no Content-Length) branch.
        if (ctx.Request.Url!.AbsolutePath.Contains("stream"))
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var outStream = ctx.Response.OutputStream;
            for (int i = 0; i < 3; i++)
            {
                var chunk = Encoding.UTF8.GetBytes($"data: chunk{i}\n\n");
                await outStream.WriteAsync(chunk);
                await outStream.FlushAsync();
            }
            ctx.Response.Close();
            continue;
        }

        var payload = $"UP method={ctx.Request.HttpMethod} path={ctx.Request.Url!.PathAndQuery} auth={ctx.Request.Headers["Authorization"]} body={body}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 201;
        ctx.Response.ContentType = "text/plain";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
});

// 2) Configure the gateway: route "/myapi" -> the fake upstream, no upstream proxy.
var config = new AppConfig
{
    Port = gatewayPort,
    Mode = ProxyMode.ReverseProxy,
    UpstreamProxy = new UpstreamProxyConfig { Enabled = false },
    Routes = new List<ApiRoute>
    {
        new ApiRoute { Name = "MyApi", PathPrefix = "/myapi", TargetBaseUrl = $"http://127.0.0.1:{upstreamPort}", Enabled = true },
    },
};

var log = new LogService();
var gateway = new ReverseProxyService(config, log);
gateway.Message += m => Console.WriteLine($"[gateway] {m}");
gateway.Start(gatewayPort);
await Task.Delay(300);

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} {detail}");
    if (!ok) failures++;
}

using var client = new HttpClient();

// 3a) POST with body + auth header through the gateway.
var post = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{gatewayPort}/myapi/v1/chat?x=1")
{
    Content = new StringContent("hello-body", Encoding.UTF8, "application/json"),
};
post.Headers.TryAddWithoutValidation("Authorization", "Bearer test-key");
var resp = await client.SendAsync(post);
var text = await resp.Content.ReadAsStringAsync();

Check("status forwarded (201)", (int)resp.StatusCode == 201, $"got {(int)resp.StatusCode}");
Check("path prefix stripped -> /v1/chat?x=1", text.Contains("path=/v1/chat?x=1"), text);
Check("method preserved (POST)", text.Contains("method=POST"));
Check("auth header preserved", text.Contains("auth=Bearer test-key"));
Check("body preserved", text.Contains("body=hello-body"));

// 3b) Unmatched path -> 502.
var miss = await client.GetAsync($"http://127.0.0.1:{gatewayPort}/nope/x");
Check("unmatched route -> 502", (int)miss.StatusCode == 502, $"got {(int)miss.StatusCode}");

// 3c) Streaming (event-stream, unknown length) forwarded + reassembled correctly.
var stream = await client.GetAsync($"http://127.0.0.1:{gatewayPort}/myapi/v1/stream",
    HttpCompletionOption.ResponseHeadersRead);
var streamText = await stream.Content.ReadAsStringAsync();
Check("stream status 200", (int)stream.StatusCode == 200, $"got {(int)stream.StatusCode}");
Check("stream content-type passthrough", stream.Content.Headers.ContentType?.MediaType == "text/event-stream");
Check("stream chunks reassembled", streamText.Contains("chunk0") && streamText.Contains("chunk1") && streamText.Contains("chunk2"), streamText.Replace("\n", "\\n"));

// 4) Log recorded the forwarded requests (newest-first).
await Task.Delay(200);
Check("log has entries", log.Entries.Count >= 3, $"count={log.Entries.Count}");
var post201 = log.Entries.FirstOrDefault(e => e.Provider == "MyApi" && e.StatusCode == 201);
Check("POST logged & completed (201)", post201 is { Completed: true },
    post201 is null ? "no completed 201 MyApi entry" : $"completed={post201.Completed}");
var stream200 = log.Entries.FirstOrDefault(e => e.Provider == "MyApi" && e.StatusCode == 200);
Check("stream logged & completed (200)", stream200 is { Completed: true },
    stream200 is null ? "no completed 200 MyApi entry" : $"completed={stream200.Completed}");

gateway.Stop();
upstream.Stop();

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} CHECK(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
