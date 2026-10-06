using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>全局限速闸（设计 §3.2 步 3）：令牌桶耗尽 / 连续补充（小数累计）/ 两类事件共用。</summary>
[TestClass]
public sealed class RateLimitTests
{
	[TestMethod]
	public async Task Rate_ExhaustsThenContinuouslyRefills()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0, perMinute: 3)), notifier, fake);

		// 初始满桶 = max_per_minute = 3
		for (int i = 0; i < 3; i++)
		{
			Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect($"10.0.0.{i}", 1000 + i, 2222, "").Result);
		}

		NotifyEnqueueResult exhausted = pipeline.EnqueueConnect("10.0.0.9", 1009, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedRate, exhausted.Result, "令牌耗尽");

		// 连续补充：3/分钟 = 0.05/秒；20s 恰好补 1 个
		fake.Advance(TimeSpan.FromSeconds(20));
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("10.0.0.10", 1010, 2222, "").Result);
		Assert.AreEqual(
			NotifyResult.SuppressedRate,
			pipeline.EnqueueConnect("10.0.0.11", 1011, 2222, "").Result,
			"补充后仍只够 1 个");

		// 小数累计：10s 只补 0.5 个 → 不足；再 10s 累计到 1 个 → 放行
		fake.Advance(TimeSpan.FromSeconds(10));
		Assert.AreEqual(
			NotifyResult.SuppressedRate,
			pipeline.EnqueueConnect("10.0.0.12", 1012, 2222, "").Result,
			"0.5 个令牌不足");
		fake.Advance(TimeSpan.FromSeconds(10));
		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueConnect("10.0.0.13", 1013, 2222, "").Result,
			"小数累计到 1 个令牌");
	}

	[TestMethod]
	public async Task Rate_BucketSharedByConnectAndClose()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0, perMinute: 2)), notifier, fake);

		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("1.0.0.1", 1000, 2222, "").Result);
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueClose("1.0.0.1", 1000, 1.0, 16, true).Result);

		NotifyEnqueueResult third = pipeline.EnqueueConnect("1.0.0.2", 1001, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedRate, third.Result, "connect 与 close 共用同一只桶");
	}
}
