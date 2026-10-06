using System.Net;
using System.Text;
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
	public void Telegram_IsEnabled_FollowsConfig()
	{
		using TelegramNotifier notifier = new(handler: new RecordingHandler());
		Assert.IsFalse(notifier.IsEnabled(TestSupport.Notifications(enabled: false).Notifications));
		Assert.IsTrue(notifier.IsEnabled(TestSupport.Notifications(enabled: true).Notifications));
	}
}
