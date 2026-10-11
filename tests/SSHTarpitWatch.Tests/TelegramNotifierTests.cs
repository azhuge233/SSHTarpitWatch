using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>Telegram 渠道（设计 §3.2 步 5 / §3.5 / §9）：URL 与 JSON、429 retry_after、错误脱敏。</summary>
[TestClass]
public sealed class TelegramNotifierTests
{
	private static NotificationConfig Config(string token = "12345:ABC", string chatId = "42") =>
		TestSupport.Notifications(token: token, chatId: chatId).Notifications;

	[TestMethod]
	public async Task Telegram_PostsToBotTokenUrl_WithChatIdAndText()
	{
		RecordingHandler handler = new();
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("hello 🕳", Config(), CancellationToken.None);

		Assert.IsTrue(result.Success);
		Assert.AreEqual(1, handler.Requests.Count);
		Assert.AreEqual("https://api.telegram.org/bot12345:ABC/sendMessage", handler.Requests[0].Url);
		StringAssert.Contains(handler.Requests[0].Body, "\"chat_id\":\"42\"");
		StringAssert.Contains(handler.Requests[0].Body, "\"parse_mode\":\"HTML\"");
		StringAssert.Contains(handler.Requests[0].Body, "hello");
	}

	[TestMethod]
	public async Task Telegram_CustomApiBase_UsedForRequest()
	{
		RecordingHandler handler = new();
		using TelegramNotifier notifier = new("http://127.0.0.1:8081/", handler);

		await notifier.SendAsync("x", Config(), CancellationToken.None);

		Assert.AreEqual("http://127.0.0.1:8081/bot12345:ABC/sendMessage", handler.Requests[0].Url);
	}

	[TestMethod]
	public async Task Telegram_429_ParsesRetryAfter()
	{
		RecordingHandler handler = new()
		{
			Responder = _ => new HttpResponseMessage((HttpStatusCode)429)
			{
				Content = new StringContent(
					"{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests: retry after 17\","
					+ "\"parameters\":{\"retry_after\":17}}",
					Encoding.UTF8,
					"application/json"),
			},
		};
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("x", Config(), CancellationToken.None);

		Assert.IsFalse(result.Success);
		Assert.AreEqual(TimeSpan.FromSeconds(17), result.RetryAfter);
		StringAssert.Contains(result.Error, "429");
		StringAssert.Contains(result.Error, "Too Many Requests");
	}

	[TestMethod]
	public async Task Telegram_ServerError_ReturnsFailureWithoutToken()
	{
		string token = "SECRET-TOKEN-42";
		RecordingHandler handler = new()
		{
			Responder = _ => throw new HttpRequestException($"connection refused for bot{token}/sendMessage"),
		};
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("x", Config(token: token), CancellationToken.None);

		Assert.IsFalse(result.Success);
		Assert.IsFalse(result.Error.Contains(token, StringComparison.Ordinal), "错误信息不得包含 token（§9）");
		StringAssert.Contains(result.Error, "<hidden>");
	}

	[TestMethod]
	public async Task Telegram_HttpError_ReturnsStatusCode()
	{
		RecordingHandler handler = new()
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request\"}"),
			},
		};
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("x", Config(), CancellationToken.None);

		Assert.IsFalse(result.Success);
		Assert.IsNull(result.RetryAfter, "非 429 无 retry_after");
		StringAssert.Contains(result.Error, "400");
	}

	[TestMethod]
	public async Task Telegram_HtmlEntityRejection_ResendsAsPlainText()
	{
		// 设计 §3.3 兜底：实体 / 解析类 400 → 去标记 + 反转义、不带 parse_mode 重发一次
		int calls = 0;
		RecordingHandler handler = new()
		{
			Responder = _ => ++calls == 1
				? new HttpResponseMessage(HttpStatusCode.BadRequest)
				{
					Content = new StringContent(
						"{\"ok\":false,\"error_code\":400,\"description\":"
						+ "\"Bad Request: can't parse entities: unsupported start tag at byte offset 3\"}",
						Encoding.UTF8,
						"application/json"),
				}
				: new HttpResponseMessage(HttpStatusCode.OK),
		};
		using TelegramNotifier notifier = new(handler: handler);

		string text = "客户端：SSH-2.0-&lt;b&gt; &amp; (<code>AS15169</code>)";
		NotifySendResult result = await notifier.SendAsync(text, Config(), CancellationToken.None);

		Assert.IsTrue(result.Success, "降级重发成功");
		Assert.AreEqual(2, handler.Requests.Count, "只重发一次");
		using JsonDocument first = JsonDocument.Parse(handler.Requests[0].Body);
		Assert.AreEqual("HTML", first.RootElement.GetProperty("parse_mode").GetString(), "首次按 HTML 发送");
		Assert.AreEqual(text, first.RootElement.GetProperty("text").GetString());
		using JsonDocument second = JsonDocument.Parse(handler.Requests[1].Body);
		Assert.IsFalse(second.RootElement.TryGetProperty("parse_mode", out _), "重发不带 parse_mode");
		Assert.AreEqual(
			"客户端：SSH-2.0-<b> & (AS15169)",
			second.RootElement.GetProperty("text").GetString(),
			"去标记 + 反转义");
	}

	[TestMethod]
	public async Task Telegram_HtmlEntityRejection_FallbackFailure_IsReported()
	{
		// 重发仍失败 → 走原失败路径（失败返回值交回管道；不再发第三次）
		RecordingHandler handler = new()
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent(
					"{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: can't parse entities\"}"),
			},
		};
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("x <code>AS1</code>", Config(), CancellationToken.None);

		Assert.IsFalse(result.Success);
		Assert.AreEqual(2, handler.Requests.Count, "降级重发只做一次");
		StringAssert.Contains(result.Error, "400");
	}

	[TestMethod]
	public async Task Telegram_BadRequest_NotParseRelated_NoResend()
	{
		// 400 但非实体 / 解析类（如 chat not found）→ 不降级重发
		RecordingHandler handler = new()
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent(
					"{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}"),
			},
		};
		using TelegramNotifier notifier = new(handler: handler);

		NotifySendResult result = await notifier.SendAsync("x", Config(), CancellationToken.None);

		Assert.IsFalse(result.Success);
		Assert.AreEqual(1, handler.Requests.Count, "非实体 / 解析类不重发");
		StringAssert.Contains(result.Error, "chat not found");
	}

	[TestMethod]
	public void Telegram_IsEnabled_FollowsConfig()
	{
		using TelegramNotifier notifier = new(handler: new RecordingHandler());
		Assert.IsFalse(notifier.IsEnabled(TestSupport.Notifications(enabled: false).Notifications));
		Assert.IsTrue(notifier.IsEnabled(TestSupport.Notifications(enabled: true).Notifications));
	}
}
