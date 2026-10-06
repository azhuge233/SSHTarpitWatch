using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>有界队列与退出排空（设计 §3.2 步 4/6）：队满丢新 + 计数；排空超时记 dropped_pending。</summary>
[TestClass]
public sealed class QueueAndDrainTests
{
	[TestMethod]
	public async Task QueueFull_DropsNewest_AndCounts()
	{
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new() { BlockFirstCall = new TaskCompletionSource() };
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0, perMinute: 5000)), notifier, fake);

		// 第一条被工作器取走并阻塞在渠道里
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("10.0.0.1", 1, 2222, "").Result);
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "工作器取走第一条");

		// 把队列填满（容量 1024）
		for (int i = 0; i < NotifyPipeline.QueueCapacity; i++)
		{
			Assert.AreEqual(
				NotifyResult.Queued,
				pipeline.EnqueueConnect("10.0.0.2", 2, 2222, "").Result,
				$"第 {i + 1} 条应能入队");
		}

		// 队满 → 丢新 + 计数（过闸标记不回滚）
		NotifyEnqueueResult overflow = pipeline.EnqueueConnect("10.0.0.3", 3, 2222, "");
		Assert.AreEqual(NotifyResult.SuppressedQueue, overflow.Result);
		Assert.IsTrue(overflow.GatePassed);

		// 放行并排空
		notifier.BlockFirstCall.SetResult();
		await TestSupport.WaitUntilAsync(() => pipeline.PendingCount == 0, "队列排空", 20000);
		Assert.AreEqual(NotifyPipeline.QueueCapacity + 1, notifier.CallCount, "在途 + 入队全部发送");
	}

	[TestMethod]
	public async Task Shutdown_DrainTimeout_LogsDroppedPending()
	{
		using ConsoleCapture log = new();
		FakeTimeProvider fake = new(TestSupport.Start);
		FakeNotifier notifier = new() { BlockFirstCall = new TaskCompletionSource() };
		await using NotifyPipeline pipeline = new(
			TestSupport.Store(TestSupport.Notifications(dedupSeconds: 0)), notifier, fake);

		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("10.0.0.1", 1, 2222, "").Result);
		Assert.AreEqual(NotifyResult.Queued, pipeline.EnqueueConnect("10.0.0.2", 2, 2222, "").Result);
		await TestSupport.WaitUntilAsync(() => notifier.CallCount >= 1, "工作器阻塞在第一条");
		Assert.AreEqual(2, pipeline.PendingCount, "在途 1 + 排队 1");

		Task shutdown = pipeline.ShutdownAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(50);
		Assert.IsFalse(shutdown.IsCompleted, "排空窗口内不应提前结束");

		fake.Advance(TimeSpan.FromSeconds(5)); // 排空超时
		await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
		StringAssert.Contains(log.Text, "NOTIFY dropped_pending=2");
	}
}
