using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 端到端（设计 §3.5/§7）：进程内假 Telegram 端点（HttpListener）+ 注入 handler，
/// 断言请求 URL 为 …/bot&lt;token&gt;/sendMessage、JSON 含 chat_id/text，且日志无 token。
/// </summary>
[TestClass]
public sealed class EndToEndTests
{
	[TestMethod]
	public async Task EndToEnd_FakeTelegramEndpoint_ReceivesRequestAndLogsNoToken()
	{
		using ConsoleCapture log = new();
		string token = "123456:TEST-TOKEN";
		int port = TestSupport.FreePort();
		using HttpListener listener = new();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();
		Task<RecordedRequest> server = AcceptOneAsync(listener);

		// 真 TelegramNotifier 打到本地假端点（注入 handler + API 基址覆盖，生产恒为 api.telegram.org）
		using TelegramNotifier notifier = new(
			$"http://127.0.0.1:{port}", new SocketsHttpHandler());
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0, token: token)), notifier);

		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("127.0.0.1", 5555, 2222, "SSH-2.0-e2e").Result);
		RecordedRequest captured = await server.WaitAsync(TimeSpan.FromSeconds(10));
		await TestSupport.WaitUntilAsync(() => pipeline.PendingCount == 0, "通知发送完成");

		Assert.AreEqual($"/bot{token}/sendMessage", captured.Url);
		StringAssert.Contains(captured.Body, "\"chat_id\":\"42\"");
		StringAssert.Contains(captured.Body, "SSH-2.0-e2e");

		StringAssert.Contains(log.Text, "NOTIFY kind=connect host=127.0.0.1 result=queued");
		StringAssert.Contains(log.Text, "result=sent");
		Assert.IsFalse(log.Text.Contains(token, StringComparison.Ordinal), "日志不得包含 token（§9）");
	}

	private static async Task<RecordedRequest> AcceptOneAsync(HttpListener listener)
	{
		HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
		string path = context.Request.Url?.AbsolutePath ?? "";
		string body;
		using (StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
		{
			body = await reader.ReadToEndAsync();
		}

		byte[] ok = Encoding.UTF8.GetBytes("{\"ok\":true,\"result\":{\"message_id\":1}}");
		context.Response.StatusCode = 200;
		context.Response.ContentType = "application/json";
		context.Response.ContentLength64 = ok.Length;
		await context.Response.OutputStream.WriteAsync(ok);
		context.Response.Close();
		return new RecordedRequest(path, body);
	}
}
