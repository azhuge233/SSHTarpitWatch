using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>消息模板（设计 §3.3）：连接/断开/昵称回退/无 banner（—）/时长与字节/UTC 偏移。</summary>
[TestClass]
public sealed class MessageTemplateTests
{
	[TestMethod]
	public void Template_Connect_ContainsAllFields()
	{
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Connect,
			Host = "203.0.113.7",
			Port = 54321,
			LocalPort = 2222,
			Banner = "SSH-2.0-libssh2_1.11.0",
			TodayCount = 3,
			TimeUtc = new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero),
			ConnectTimeUtc = new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero),
		};
		DateTimeOffset local = new(2026, 10, 6, 9, 2, 3, TimeSpan.FromHours(8));
		string text = NotifyMessage.Build(evt, "home-vps", local);

		Assert.AreEqual(
			"🕳 home-vps · SSHTarpitWatch · 新连接\n"
			+ "来源：203.0.113.7:54321 → 本机 :2222\n"
			+ "客户端：SSH-2.0-libssh2_1.11.0\n"
			+ "时间：2026-10-06 09:02:03 (UTC+08:00)\n"
			+ "该 IP 今日第 3 次",
			text);
	}

	[TestMethod]
	public void Template_Connect_NoBanner_ShowsDash()
	{
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Connect,
			Host = "203.0.113.7",
			Port = 54321,
			LocalPort = 2222,
			Banner = "",
			TodayCount = 1,
		};
		string text = NotifyMessage.Build(evt, "home-vps", TestSupport.Start);
		StringAssert.Contains(text, $"客户端：{NotifyMessage.NoBanner}");
	}

	[TestMethod]
	public void Template_Close_MergedTemplate_ContainsAllConnectionFields()
	{
		// 断开条为合并模板（设计 §3.3）：来源→本机端口 / 客户端 / 时间=连接时刻 / 停留 / 字节 / 当日 N
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Close,
			Host = "198.51.100.9",
			Port = 40000,
			LocalPort = 2222,
			Banner = "SSH-2.0-libssh2_1.11.0",
			TodayCount = 3,
			TimeUtc = new DateTimeOffset(2026, 10, 11, 1, 13, 0, TimeSpan.Zero),
			ConnectTimeUtc = new DateTimeOffset(2026, 10, 11, 1, 12, 0, TimeSpan.Zero),
			DurationSeconds = 3661.8,
			BytesSent = 12345,
		};
		DateTimeOffset local = new(2026, 10, 11, 9, 12, 0, TimeSpan.FromHours(8));
		string text = NotifyMessage.Build(evt, "box", local);

		// 「时间」取连接建立时刻（09:12:00，而非断开的 09:13:00）
		Assert.AreEqual(
			"🕳 box · SSHTarpitWatch · 连接断开\n"
			+ "来源：198.51.100.9:40000 → 本机 :2222\n"
			+ "客户端：SSH-2.0-libssh2_1.11.0\n"
			+ "时间：2026-10-11 09:12:00 (UTC+08:00)\n"
			+ "停留：1 小时 1 分 1 秒\n"
			+ "发出：12345 字节\n"
			+ "该 IP 今日第 3 次",
			text);
	}

	[TestMethod]
	public void Template_Close_NoBanner_ShowsDash()
	{
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Close,
			Host = "198.51.100.9",
			Port = 40000,
			LocalPort = 2222,
			Banner = "",
			TodayCount = 1,
			DurationSeconds = 4.655,
			BytesSent = 96,
		};
		string text = NotifyMessage.Build(evt, "box", TestSupport.Start);

		StringAssert.Contains(text, $"客户端：{NotifyMessage.NoBanner}");
		StringAssert.Contains(text, "停留：0 小时 0 分 4 秒");
		StringAssert.Contains(text, "发出：96 字节");
	}

	[TestMethod]
	public void Template_Duration_CoversShortAndZero()
	{
		Assert.AreEqual("0 小时 0 分 4 秒", NotifyMessage.FormatDuration(4.655));
		Assert.AreEqual("0 小时 0 分 0 秒", NotifyMessage.FormatDuration(0));
		Assert.AreEqual("2 小时 0 分 0 秒", NotifyMessage.FormatDuration(7200));
	}

	[TestMethod]
	public void Template_NicknameEmpty_FallsBackToMachineName()
	{
		Assert.AreEqual(Environment.MachineName, NotifyMessage.ResolveNickname(""));
		Assert.AreEqual(Environment.MachineName, NotifyMessage.ResolveNickname("   "));
		Assert.AreEqual("named", NotifyMessage.ResolveNickname("named"));

		NotifyEvent evt = new() { Kind = NotifyEventKind.Connect, Host = "1.2.3.4", Port = 1, LocalPort = 2222 };
		string text = NotifyMessage.Build(evt, NotifyMessage.ResolveNickname(""), TestSupport.Start);
		StringAssert.Contains(text, $"🕳 {Environment.MachineName} · SSHTarpitWatch · 新连接");
	}

	[TestMethod]
	public void Template_Offset_Formatting()
	{
		Assert.AreEqual("+00:00", NotifyMessage.FormatOffset(TimeSpan.Zero));
		Assert.AreEqual("+08:00", NotifyMessage.FormatOffset(TimeSpan.FromHours(8)));
		Assert.AreEqual("-05:00", NotifyMessage.FormatOffset(TimeSpan.FromHours(-5)));
		Assert.AreEqual("+05:30", NotifyMessage.FormatOffset(TimeSpan.FromMinutes(330)));
	}

	[TestMethod]
	public void Template_TestNotification_IsDistinctFromEvents()
	{
		DateTimeOffset local = new(2026, 10, 6, 9, 2, 3, TimeSpan.FromHours(8));
		string text = NotifyMessage.BuildTest("home-vps", local);
		StringAssert.Contains(text, "测试通知");
		StringAssert.Contains(text, "时间：2026-10-06 09:02:03 (UTC+08:00)");
		Assert.IsFalse(text.Contains("新连接", StringComparison.Ordinal));
	}
}
