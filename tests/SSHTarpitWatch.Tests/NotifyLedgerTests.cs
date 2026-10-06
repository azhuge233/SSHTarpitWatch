using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>去重账本与当日计数（设计 §3.2 步 1/2、§3.4）：跨日重置、定期清理、硬上限最旧淘汰。</summary>
[TestClass]
public sealed class NotifyLedgerTests
{
	[TestMethod]
	public void Ledger_DailyCount_ResetsAcrossLocalDays()
	{
		NotifyLedger ledger = new();
		DateTimeOffset day1 = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(8));

		Assert.AreEqual(1, ledger.RegisterConnect("1.2.3.4", day1));
		Assert.AreEqual(2, ledger.RegisterConnect("1.2.3.4", day1.AddHours(1)));
		Assert.AreEqual(1, ledger.RegisterConnect("9.9.9.9", day1), "按 IP 独立计数");
		Assert.AreEqual(1, ledger.RegisterConnect("1.2.3.4", day1.AddDays(1)), "跨本地日重置");
	}

	[TestMethod]
	public void Ledger_Cleanup_RemovesExpiredCooldownAndCrossDayCounts()
	{
		NotifyLedger ledger = new();
		DateTimeOffset now = TestSupport.Start;
		Assert.IsTrue(ledger.TryPassDedup("1.2.3.4", now, 3600));
		Assert.AreEqual(1, ledger.RegisterConnect("1.2.3.4", now));
		Assert.AreEqual(1, ledger.DedupCount);
		Assert.AreEqual(1, ledger.DailyCount);

		ledger.Cleanup(now.AddDays(1).AddSeconds(1), now.AddDays(1).AddSeconds(1), 3600);

		Assert.AreEqual(0, ledger.DedupCount, "过期去重条目清除");
		Assert.AreEqual(0, ledger.DailyCount, "跨日计数条目清除");
	}

	[TestMethod]
	public void Ledger_Cleanup_KeepsFreshEntries()
	{
		NotifyLedger ledger = new();
		DateTimeOffset now = TestSupport.Start;
		ledger.TryPassDedup("1.2.3.4", now, 3600);

		ledger.Cleanup(now.AddSeconds(3599), now, 3600);

		Assert.AreEqual(1, ledger.DedupCount, "未过期条目保留");
		Assert.IsFalse(ledger.TryPassDedup("1.2.3.4", now.AddSeconds(3599), 3600), "冷却仍有效");
	}

	[TestMethod]
	public void Ledger_Capacity_EvictsOldest()
	{
		NotifyLedger ledger = new(capacity: 4);
		DateTimeOffset t = TestSupport.Start;
		ledger.MarkPassed("a", t);
		ledger.MarkPassed("b", t.AddSeconds(1));
		ledger.MarkPassed("c", t.AddSeconds(2));
		ledger.MarkPassed("d", t.AddSeconds(3));
		Assert.IsTrue(ledger.DedupCount <= 4, $"达到上限即淘汰：{ledger.DedupCount}");

		ledger.MarkPassed("e", t.AddSeconds(4));
		Assert.IsTrue(ledger.DedupCount <= 4, "硬上限不超");

		Assert.IsTrue(ledger.TryPassDedup("a", t.AddSeconds(5), 3600), "最旧的 a 已淘汰");
		Assert.IsFalse(ledger.TryPassDedup("e", t.AddSeconds(5), 3600), "最新的 e 仍在冷却");
	}
}
