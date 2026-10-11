using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Geo;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 消息模板的地区行 / ISP 行（设计 §3.3 / §3.6，逐字符断言）：逐级省略规则、名称全空只显示国家码、
/// 四级降级（未启用无行 / 库缺失 / 无查询结果 / 有结果）、ISP 行仅非空出现。
/// </summary>
[TestClass]
public sealed class GeoMessageTests
{
	private static readonly DateTimeOffset local = new(2026, 10, 11, 9, 12, 0, TimeSpan.FromHours(8));

	[TestMethod]
	public void Template_Close_RegionAndIsp_ExactText()
	{
		NotifyEvent evt = CloseEvent(GeoLookupResult.Found(GeoResult("中国", "CN", "广东省", "深圳市", "电信")));

		string text = NotifyMessage.Build(evt, "box", local);

		Assert.AreEqual(
			"🕳 box · SSHTarpitWatch · 连接断开\n"
			+ "来源：198.51.100.9:40000 → 本机 :2222\n"
			+ "客户端：SSH-2.0-libssh2_1.11.0\n"
			+ "地区：中国 · 广东省 · 深圳市 (CN)\n"
			+ "ISP：电信\n"
			+ "时间：2026-10-11 09:12:00 (UTC+08:00)\n"
			+ "停留：1 小时 1 分 1 秒\n"
			+ "发出：12345 字节\n"
			+ "该 IP 今日第 3 次",
			text);
	}

	[TestMethod]
	public void Template_Connect_RegionAndIsp_ExactText()
	{
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Connect,
			Host = "198.51.100.9",
			Port = 40000,
			LocalPort = 2222,
			Banner = "SSH-2.0-test",
			TodayCount = 2,
			TimeUtc = local,
			ConnectTimeUtc = local,
			Geo = GeoLookupResult.Found(GeoResult("中国", "CN", "广东省", "深圳市", "电信")),
		};

		string text = NotifyMessage.Build(evt, "box", local);

		Assert.AreEqual(
			"🕳 box · SSHTarpitWatch · 新连接\n"
			+ "来源：198.51.100.9:40000 → 本机 :2222\n"
			+ "客户端：SSH-2.0-test\n"
			+ "地区：中国 · 广东省 · 深圳市 (CN)\n"
			+ "ISP：电信\n"
			+ "时间：2026-10-11 09:12:00 (UTC+08:00)\n"
			+ "该 IP 今日第 2 次",
			text);
	}

	[TestMethod]
	public void Template_Region_OmissionRules()
	{
		// 逐字符断言地区行（逐级省略：空值不参与、分隔符只在有值时出现）
		Assert.AreEqual("地区：中国", RegionLine(GeoResult("中国", "", "", "", "")), "只有国家名");
		Assert.AreEqual("地区：中国 · 深圳市 (CN)", RegionLine(GeoResult("中国", "CN", "", "深圳市", "")), "省为空 → 中国 · 深圳市 (CN)");
		Assert.AreEqual("地区：CN", RegionLine(GeoResult("", "CN", "", "", "")), "名称全空但码非空 → 只显示码");
		Assert.AreEqual("地区：中国 · 广东省", RegionLine(GeoResult("中国", "", "广东省", "", "")), "无码 → 不显示括号");
	}

	[TestMethod]
	public void Template_DegradedStates()
	{
		Assert.IsFalse(CloseText(GeoLookupResult.Disabled).Contains("地区：", StringComparison.Ordinal), "enabled=false → 整行不出现");
		Assert.IsFalse(CloseText(GeoLookupResult.Disabled).Contains("ISP：", StringComparison.Ordinal));

		Assert.IsTrue(
			CloseText(GeoLookupResult.Missing).Contains($"\n地区：{NotifyMessage.GeoMissing}\n", StringComparison.Ordinal),
			"库不可读 → 库缺失");
		Assert.IsTrue(
			CloseText(GeoLookupResult.NoRecord).Contains($"\n地区：{NotifyMessage.GeoNoRecord}\n", StringComparison.Ordinal),
			"库可读但无记录 → 无查询结果");

		// 默认值（手工构造的事件 / 未接线的旧路径）与未启用同语义：不渲染地区内容
		NotifyEvent plain = CloseEvent(default);
		Assert.IsFalse(NotifyMessage.Build(plain, "box", local).Contains("地区：", StringComparison.Ordinal));
	}

	[TestMethod]
	public void Template_IspLine_OnlyWhenPresent()
	{
		string withoutIsp = CloseText(GeoResult("中国", "CN", "广东省", "深圳市", ""));
		Assert.IsFalse(withoutIsp.Contains("ISP：", StringComparison.Ordinal), "库没给运营商 → 不出现 ISP 行");

		string withIsp = CloseText(GeoResult("中国", "CN", "广东省", "深圳市", "电信"));
		StringAssert.Contains(withIsp, "\nISP：电信\n");
	}

	[TestMethod]
	public void Template_IspOnlyResult_NoRegionLine()
	{
		// 只有 ASN/ISP 的库（如 DB-IP ASN）：地区行不出现（无地区信息），ISP 行照常
		string text = CloseText(GeoResult("", "", "", "", "AS13335 CLOUDFLARENET"));

		Assert.IsFalse(text.Contains("地区：", StringComparison.Ordinal));
		StringAssert.Contains(text, "\nISP：AS13335 CLOUDFLARENET\n");
		Assert.IsTrue(
			NotifyMessage.Build(CloseEvent(default), "box", local).Contains("客户端：", StringComparison.Ordinal),
			"（对照）地区行缺失不影响其余模板行");
	}

	/// <summary>取出消息里的地区行（逐字符断言的载体）。</summary>
	private static string RegionLine(GeoResult result) => CloseText(result)
		.Split('\n')
		.First(line => line.StartsWith("地区：", StringComparison.Ordinal));

	private static string CloseText(GeoLookupResult geo) => NotifyMessage.Build(CloseEvent(geo), "box", local);

	private static string CloseText(GeoResult result) => CloseText(GeoLookupResult.Found(result));

	private static GeoResult GeoResult(string country, string code, string region, string city, string isp) =>
		new(country, code, region, city, isp, "test.mmdb");

	private static NotifyEvent CloseEvent(GeoLookupResult geo) => new()
	{
		Kind = NotifyEventKind.Close,
		Host = "198.51.100.9",
		Port = 40000,
		LocalPort = 2222,
		Banner = "SSH-2.0-libssh2_1.11.0",
		TodayCount = 3,
		TimeUtc = local,
		ConnectTimeUtc = local,
		DurationSeconds = 3661.8,
		BytesSent = 12345,
		Geo = geo,
	};
}
