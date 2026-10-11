using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 时长过滤（设计 §3.2 步 0 / §7 / R3.4）：只作用于断开条；停留 ≥ 阈值才通知；
/// 过滤先于去重闸、不写账本；被过滤的连接仍计入当日 N。
/// </summary>
[TestClass]
public sealed class DurationFilterTests
{
	[TestMethod]
	public async Task Duration_MinZero_NoFiltering()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: false, minDurationSeconds: 0)), notifier, fake);

		ConnectEnqueueResult connect = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "");
		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueClose(connect.Snapshot, 0.001, 16).Result,
			"min_duration_seconds=0 = 不过滤");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "断开通知送达");
	}

	[TestMethod]
	public async Task Duration_BelowThreshold_SuppressedDuration_AndLedgerNotWritten()
	{
		using ConsoleCapture log = new();
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: false, minDurationSeconds: 60, dedupSeconds: 3600)),
			notifier,
			fake);

		ConnectEnqueueResult shortLived = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "");
		Assert.AreEqual(
			NotifyResult.SuppressedDuration,
			pipeline.EnqueueClose(shortLived.Snapshot, 59.9, 16).Result,
			"停留 < 阈值 → 丢弃 + 计数（结果码 suppressed_duration）");
		StringAssert.Contains(log.Text, "NOTIFY kind=close host=1.2.3.4 result=suppressed_duration");

		// 不写账本（设计 §3.2 步 0）：同一 IP 随后一条长连接不被每 IP 冷却压制
		ConnectEnqueueResult longLived = pipeline.EnqueueConnect("1.2.3.4", 1001, 2222, "");
		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueClose(longLived.Snapshot, 60.5, 32).Result,
			"被过滤的连接不占用冷却额度");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "长连接通知送达");
	}

	[TestMethod]
	public async Task Duration_AtThreshold_Notifies()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: false, minDurationSeconds: 60)), notifier, fake);

		ConnectEnqueueResult connect = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "");
		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueClose(connect.Snapshot, 60.0, 32).Result,
			"停留 = 阈值 → 通知（≥ 语义）");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "断开通知送达");
	}

	[TestMethod]
	public async Task Duration_FilteredConnection_StillCountsTowardTodayN()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: false, minDurationSeconds: 60, dedupSeconds: 0)),
			notifier,
			fake);

		ConnectEnqueueResult dropped = pipeline.EnqueueConnect("7.7.7.7", 1000, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedDuration, pipeline.EnqueueClose(dropped.Snapshot, 1.0, 16).Result);

		ConnectEnqueueResult kept = pipeline.EnqueueConnect("7.7.7.7", 1001, 2222, "");
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueClose(kept.Snapshot, 90.0, 64).Result);

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "长连接通知送达");
		StringAssert.Contains(notifier.Texts[0], "该 IP 今日第 2 次", "被过滤的连接仍计入当日 N（R3.4）");
	}

	[TestMethod]
	public async Task Duration_OnlyAppliesToCloseEvents()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(onConnect: true, minDurationSeconds: 999)), notifier, fake);

		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "").Result,
			"时长闸只作用于断开条——连接条不受影响（R3.3）");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "连接通知送达");
	}

	[TestMethod]
	public async Task Duration_ReadsCurrentConfigAtCloseTime()
	{
		// 判定读「断开时刻」的当前快照：改 min_duration_seconds（SIGHUP 热重载）对存量连接即时生效（设计 §4.3）
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new();
		ConfigStore store = TestSupport.Store(TestSupport.Notifications(onConnect: false, minDurationSeconds: 999));
		await using NotifyPipeline pipeline = new(store, notifier, fake);

		ConnectEnqueueResult connect = pipeline.EnqueueConnect("1.2.3.4", 1000, 2222, "");
		store.Replace(TestSupport.Notifications(onConnect: false, minDurationSeconds: 60));

		Assert.AreEqual(
			NotifyResult.Queued,
			pipeline.EnqueueClose(connect.Snapshot, 120.0, 32).Result,
			"用断开时的新阈值判定（连接期快照不含阈值）");

		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "断开通知送达");
	}
}
