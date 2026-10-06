using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>发送工作器（设计 §3.2 步 5）：退避序列 2s/10s/30s、重试上限、429 retry_after。</summary>
[TestClass]
public sealed class RetryTests
{
	[TestMethod]
	public async Task Retry_BackoffSequence_2s10s30s_ThenFails()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new() { Responder = _ => NotifySendResult.Fail("HTTP 500: boom") };
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0)), notifier, fake);

		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "").Result);

		// 第 1 次（初始）+ 3 次重试，间隔 2s / 10s / 30s
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "第 1 次尝试");
		await TestSupport.WaitUntilAsync(() => fake.ActiveTimerCount >= 2, "进入第 1 次退避等待");
		await TestSupport.AdvanceExpectingNoCallAsync(fake, () => notifier.CallCount, 1, TimeSpan.FromSeconds(1.9));
		fake.Advance(TimeSpan.FromSeconds(0.1));
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 2, "2s 后退避重试");

		await TestSupport.WaitUntilAsync(() => fake.ActiveTimerCount >= 2, "进入第 2 次退避等待");
		await TestSupport.AdvanceExpectingNoCallAsync(fake, () => notifier.CallCount, 2, TimeSpan.FromSeconds(9.9));
		fake.Advance(TimeSpan.FromSeconds(0.1));
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 3, "10s 后退避重试");

		await TestSupport.WaitUntilAsync(() => fake.ActiveTimerCount >= 2, "进入第 3 次退避等待");
		await TestSupport.AdvanceExpectingNoCallAsync(fake, () => notifier.CallCount, 3, TimeSpan.FromSeconds(29.9));
		fake.Advance(TimeSpan.FromSeconds(0.1));
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 4, "30s 后退避重试");

		// 重试 ≤3 次：此后不再有新的尝试
		await TestSupport.WaitUntilAsync(() => pipeline.PendingCount == 0, "工作器放弃该条");
		fake.Advance(TimeSpan.FromMinutes(5));
		await Task.Delay(100);
		Assert.AreEqual(4, notifier.CallCount, "1 次初始 + 最多 3 次重试");
	}

	[TestMethod]
	public async Task Telegram429_WaitsRetryAfter_ThenSends()
	{
		using ConsoleCapture log = new();
		FakeTimeProvider fake = new(TestSupport.Start);
		RecordingHandler handler = new()
		{
			Responder = _ => new HttpResponseMessage((HttpStatusCode)429)
			{
				Content = new StringContent(
					"{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\","
					+ "\"parameters\":{\"retry_after\":7}}",
					Encoding.UTF8,
					"application/json"),
			},
		};
		using TelegramNotifier notifier = new(handler: handler);
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0)), notifier, fake);

		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "").Result);
		await TestSupport.WaitUntilAsync(() => handler.RequestCount >= 1, "第 1 次请求（429）");
		await TestSupport.WaitUntilAsync(() => fake.ActiveTimerCount >= 2, "进入 retry_after 等待");

		// 429 的 retry_after=7s 生效：未到点不重试，到点重试
		handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);
		await TestSupport.AdvanceExpectingNoCallAsync(fake, () => handler.RequestCount, 1, TimeSpan.FromSeconds(6.9));
		fake.Advance(TimeSpan.FromSeconds(0.1));
		await TestSupport.WaitUntilAsync(() => handler.RequestCount >= 2, "retry_after 到点后重试");
		await TestSupport.WaitUntilAsync(() => pipeline.PendingCount == 0, "最终发送成功");
		StringAssert.Contains(log.Text, "result=sent");
	}
}
