using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>管道闸门（设计 §3.2 步 0..4）：开关闸 / 当日计数 / 去重闸 / 过闸标记不回滚。</summary>
[TestClass]
public sealed class PipelineGateTests
{
	[TestMethod]
	public async Task Connect_PassesOnce_PerIpCooldownBlocksWithinWindow()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 3600)), notifier, fake);

		NotifyEnqueueResult first = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "SSH-2.0-a");
		Assert.AreEqual(NotifyResult.Queued, first.Result);
		Assert.IsTrue(first.GatePassed);

		NotifyEnqueueResult second = pipeline.EnqueueConnect("1.2.3.4", 1001, 2222, "SSH-2.0-a");
		Assert.AreEqual(NotifyResult.SuppressedDedup, second.Result, "冷却窗口内压制");
		Assert.IsFalse(second.GatePassed);

		NotifyEnqueueResult otherIp = pipeline.EnqueueConnect("5.6.7.8", 1002, 2222, "SSH-2.0-b");
		Assert.AreEqual(NotifyResult.Queued, otherIp.Result, "冷却按 IP 独立");

		fake.Advance(TimeSpan.FromSeconds(3599));
		NotifyEnqueueResult beforeWindow = pipeline.EnqueueConnect("1.2.3.4", 1003, 2222, "SSH-2.0-a");
		Assert.AreEqual(NotifyResult.SuppressedDedup, beforeWindow.Result, "窗口未到仍压制");

		fake.Advance(TimeSpan.FromSeconds(1));
		NotifyEnqueueResult expired = pipeline.EnqueueConnect("1.2.3.4", 1004, 2222, "SSH-2.0-a");
		Assert.AreEqual(NotifyResult.Queued, expired.Result, "窗口到期（≥）即放行");
	}

	[TestMethod]
	public async Task Connect_DedupZero_AlwaysPasses()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0)), notifier, fake);

		for (int i = 0; i < 3; i++)
		{
			NotifyEnqueueResult result = pipeline.EnqueueConnect("1.2.3.4", 2000 + i, 2222, "");
			Assert.AreEqual(NotifyResult.Queued, result.Result, "dedup=0 等于关闭去重");
			Assert.IsTrue(result.GatePassed);
		}
	}

	[TestMethod]
	public async Task SwitchOff_SuppressedButStillCounted()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		ConfigStore store = TestSupport.Store(TestSupport.Notifications(enabled: false, dedupSeconds: 0));
		await using NotifyPipeline pipeline = new(store, notifier, fake);

		Assert.AreEqual(NotifyResult.SuppressedSwitch, pipeline.EnqueueConnect("1.2.3.4", 1, 2222, "").Result);
		Assert.AreEqual(NotifyResult.SuppressedSwitch, pipeline.EnqueueConnect("1.2.3.4", 2, 2222, "").Result);
		Assert.AreEqual(NotifyResult.SuppressedSwitch, pipeline.EnqueueConnect("1.2.3.4", 3, 2222, "").Result);

		// 重载打开通知（设计 §4.3）：步 1 当日计数不因压制而丢——N 应为第 4 次
		store.Replace(TestSupport.Notifications(dedupSeconds: 0));
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("1.2.3.4", 4, 2222, "").Result);
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "重载后连接通知送达");
		StringAssert.Contains(notifier.Texts[0], "该 IP 今日第 4 次");
	}

	[TestMethod]
	public async Task Close_FollowsConnectGate()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 3600)), notifier, fake);

		// connect 过闸 → 本连接 close 也过闸
		NotifyEnqueueResult connect = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "");
		NotifyEnqueueResult close = pipeline.EnqueueClose("1.2.3.4", 1000, 5.0, 96, connect.GatePassed);
		Assert.AreEqual(NotifyResult.Queued, close.Result);
		Assert.IsTrue(close.GatePassed);

		// connect 被冷却压制 → 断开也不过闸（R3.5：断开条跟随其连接条）
		NotifyEnqueueResult suppressedConnect = pipeline.EnqueueConnect("1.2.3.4", 1001, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedDedup, suppressedConnect.Result);
		NotifyEnqueueResult followedClose = pipeline.EnqueueClose(
			"1.2.3.4", 1001, 3.0, 32, suppressedConnect.GatePassed);
		Assert.AreEqual(NotifyResult.SuppressedDedup, followedClose.Result);
		Assert.IsFalse(followedClose.GatePassed);

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 2, "connect+close 均已发送");
	}

	[TestMethod]
	public async Task ConnectOff_CloseOn_StillNotifiesClose()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: false, onClose: true, dedupSeconds: 3600)),
			notifier,
			fake);

		NotifyEnqueueResult connect = pipeline.EnqueueConnect("9.9.9.9", 4000, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedSwitch, connect.Result, "连接通知开关关闭");
		Assert.IsFalse(connect.GatePassed);

		NotifyEnqueueResult close = pipeline.EnqueueClose("9.9.9.9", 4000, 12.5, 480, connect.GatePassed);
		Assert.AreEqual(NotifyResult.Queued, close.Result, "关掉连接通知不应连带关掉断开通知（§3.2 步 2）");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "断开通知送达");
		StringAssert.Contains(notifier.Texts[0], "连接断开");

		// 同一 IP 的第二条断开在冷却窗口内 → 压制
		NotifyEnqueueResult second = pipeline.EnqueueClose("9.9.9.9", 4001, 1.0, 10, false);
		Assert.AreEqual(NotifyResult.SuppressedDedup, second.Result);
	}

	[TestMethod]
	public async Task GatePassed_NotRolledBackByRateLimit()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 3600, perMinute: 1)), notifier, fake);

		// 初始满桶 1 个令牌：第一条用掉，第二条被限速
		NotifyEnqueueResult first = pipeline.EnqueueConnect("1.1.1.1", 1000, 2222, "");
		Assert.AreEqual(NotifyResult.Queued, first.Result);

		NotifyEnqueueResult throttled = pipeline.EnqueueConnect("2.2.2.2", 1001, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedRate, throttled.Result);
		Assert.IsTrue(throttled.GatePassed, "过闸标记不因后续限速丢弃回滚（§3.2 步 2）");

		// 限速恢复后，close 跟随 connect 的过闸标记（该标记未被回滚）仍然成立
		fake.Advance(TimeSpan.FromSeconds(60));
		NotifyEnqueueResult close = pipeline.EnqueueClose("2.2.2.2", 1001, 2.0, 32, throttled.GatePassed);
		Assert.AreEqual(NotifyResult.Queued, close.Result);
		Assert.IsTrue(close.GatePassed);
	}
}
