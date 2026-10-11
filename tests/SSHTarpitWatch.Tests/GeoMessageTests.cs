using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Geo;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 消息模板的地区行 / ISP 行 / 标签行（设计 §3.3 / §3.6 / R3.9，逐字符断言）：逐级省略规则、
/// 名称全空只显示国家码、四级降级（未启用无行 / 库缺失 / 无查询结果 / 有结果）、
/// ISP 行三形态（名+号 / 只有名 / 只有号）、标签行（地区 tag + ISP tag、凑不出标签才 #未查到信息）、
/// HTML 转义（动态值一律转义；裸尖括号只来自我们自己插入的 code 标签）。
/// </summary>
[TestClass]
public sealed class GeoMessageTests
{
	private static readonly DateTimeOffset local = new(2026, 10, 11, 9, 12, 0, TimeSpan.FromHours(8));

	[TestMethod]
	public void Template_Close_RegionIspAsn_ExactText()
	{
		NotifyEvent evt = CloseEvent(
			GeoLookupResult.Found(GeoResult("中国", "CN", "广东省", "深圳市", "Google LLC", "15169")));

		string text = NotifyMessage.Build(evt, "box", local);

		Assert.AreEqual(
			"🕳 box · SSHTarpitWatch · 连接断开\n"
			+ "来源：198.51.100.9:40000 → 本机 :2222\n"
			+ "客户端：SSH-2.0-libssh2_1.11.0\n"
			+ "地区：中国 · 广东省 · 深圳市 (CN)\n"
			+ "ISP：Google LLC (<code>AS15169</code>)\n"
			+ "时间：2026-10-11 09:12:00 (UTC+08:00)\n"
			+ "停留：1 小时 1 分 1 秒\n"
			+ "发出：12345 字节\n"
			+ "该 IP 今日第 3 次\n"
			+ "#中国 #CN #广东省 #深圳市 #AS15169 #Google_LLC",
			text);
	}

	[TestMethod]
	public void Template_Connect_RegionIspAsn_ExactText()
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
			Geo = GeoLookupResult.Found(GeoResult("中国", "CN", "广东省", "深圳市", "Google LLC", "15169")),
		};

		string text = NotifyMessage.Build(evt, "box", local);

		Assert.AreEqual(
			"🕳 box · SSHTarpitWatch · 新连接\n"
			+ "来源：198.51.100.9:40000 → 本机 :2222\n"
			+ "客户端：SSH-2.0-test\n"
			+ "地区：中国 · 广东省 · 深圳市 (CN)\n"
			+ "ISP：Google LLC (<code>AS15169</code>)\n"
			+ "时间：2026-10-11 09:12:00 (UTC+08:00)\n"
			+ "该 IP 今日第 2 次\n"
			+ "#中国 #CN #广东省 #深圳市 #AS15169 #Google_LLC",
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
	public void Template_IspLine_Combinations()
	{
		// 设计 §3.3 / R5.3：名 + 号 → ISP：{名} (<code>AS{号}</code>)；只有其一 → 只显示其一；都没有 → 整行不出现
		Assert.AreEqual(
			"ISP：Cloudflare, Inc. (<code>AS13335</code>)",
			IspLine(GeoResult("", "", "", "", "Cloudflare, Inc.", "13335")),
			"名 + 号：组织名在前、号包 code（可点复制）");
		Assert.AreEqual(
			"ISP：Cloudflare, Inc.",
			IspLine(GeoResult("", "", "", "", "Cloudflare, Inc.", "")),
			"只有名 → 只显示名");
		Assert.AreEqual(
			"ISP：<code>AS13335</code>",
			IspLine(GeoResult("", "", "", "", "", "13335")),
			"只有号 → 只显示号");
		Assert.AreEqual("", IspLine(GeoResult("", "", "", "", "", "")), "名号都没有 → 整行不出现");

		string withoutIsp = CloseText(GeoResult("中国", "CN", "广东省", "深圳市", "", ""));
		Assert.IsFalse(withoutIsp.Contains("ISP：", StringComparison.Ordinal), "库没给运营商 → 不出现 ISP 行");
	}

	[TestMethod]
	public void Template_IspOnlyResult_NoRegionLine()
	{
		// 只有 ASN/ISP 的库（如 GeoLite2-ASN / IPinfo Lite）：地区行不出现，ISP 行照常
		string text = CloseText(GeoResult("", "", "", "", "Cloudflare, Inc.", "13335"));

		Assert.IsFalse(text.Contains("地区：", StringComparison.Ordinal));
		StringAssert.Contains(text, "\nISP：Cloudflare, Inc. (<code>AS13335</code>)\n");
		Assert.IsTrue(
			NotifyMessage.Build(CloseEvent(default), "box", local).Contains("客户端：", StringComparison.Ordinal),
			"（对照）地区行缺失不影响其余模板行");
	}

	[TestMethod]
	public void Template_TagLine_Rules()
	{
		// R3.9（2026-10-12 修订）：地区 tag + ISP tag（号在前）、净化规则；凑不出任何标签才 #未查到信息
		StringAssert.Contains(
			CloseText(GeoResult("中国", "CN", "广东省", "深圳市", "", "")),
			"\n#中国 #CN #广东省 #深圳市",
			"四级齐全（国家名 → 国家码 → 省 → 市）");
		StringAssert.Contains(CloseText(GeoResult("中国", "CN", "", "", "", "")), "\n#中国 #CN", "无省无市");
		StringAssert.Contains(CloseText(GeoResult("", "CN", "", "", "", "")), "\n#CN", "只有国家码");

		// ISP tag 追加在地区 tag 之后，号在前、组织名净化在后
		StringAssert.Contains(
			CloseText(GeoResult("中国", "CN", "广东省", "深圳市", "Google LLC", "15169")),
			"\n#中国 #CN #广东省 #深圳市 #AS15169 #Google_LLC",
			"地区 tag + ISP tag（号在前）");
		StringAssert.Contains(CloseText(GeoResult("", "", "", "", "", "13335")), "\n#AS13335", "只有号 → 只有 AS tag");
		StringAssert.Contains(CloseText(GeoResult("", "", "", "", "电信", "")), "\n#电信", "只有组织名 → 只有名 tag");

		// 净化：非字母数字 → 下划线、连续下划线合并为一个、去首尾下划线
		StringAssert.Contains(
			CloseText(GeoResult("United States", "US", "New York", "", "", "")),
			"\n#United_States #US #New_York",
			"空格 → 下划线");
		StringAssert.Contains(
			CloseText(GeoResult("", "", "", "", "Cloudflare, Inc.", "")),
			"\n#Cloudflare_Inc",
			"逗号 + 空格 → 合并为单个下划线");

		// 启用但凑不出标签（库缺失 / 无查询结果 / 字段全被净化掉）→ #未查到信息
		StringAssert.Contains(CloseText(GeoLookupResult.Missing), "\n#未查到信息");
		StringAssert.Contains(CloseText(GeoLookupResult.NoRecord), "\n#未查到信息");
		StringAssert.Contains(CloseText(GeoResult("///", "", "", "", "", "")), "\n#未查到信息", "字段净化后为空");

		// geoip 未启用（含默认值路径）→ 整行不出现
		Assert.IsFalse(CloseText(GeoLookupResult.Disabled).Contains("\n#", StringComparison.Ordinal), "未启用 → 不打标签");
		Assert.IsFalse(
			NotifyMessage.Build(CloseEvent(default), "box", local).Contains("\n#", StringComparison.Ordinal),
			"事件默认值同语义");

		// 连接条同样自动带标签行（无开关）
		NotifyEvent connect = new()
		{
			Kind = NotifyEventKind.Connect,
			Host = "198.51.100.9",
			Port = 40000,
			LocalPort = 2222,
			Banner = "SSH-2.0-test",
			TodayCount = 2,
			TimeUtc = local,
			ConnectTimeUtc = local,
			Geo = GeoLookupResult.Found(GeoResult("中国", "CN", "广东省", "深圳市", "Google LLC", "15169")),
		};
		StringAssert.Contains(
			NotifyMessage.Build(connect, "box", local),
			"\n#中国 #CN #广东省 #深圳市 #AS15169 #Google_LLC");
	}

	[TestMethod]
	public void Template_TagLine_HasNoFormatMarkers()
	{
		// R3.9：标签不加任何格式标记（否则 Telegram 不识别为可点标签）——ISP 行的 code 只出现在 ISP 行
		string text = CloseText(GeoResult("中国", "CN", "广东省", "深圳市", "Google LLC", "15169"));
		string tagLine = text.Split('\n').Last(line => line.StartsWith('#'));

		Assert.IsFalse(tagLine.Contains('<', StringComparison.Ordinal), tagLine);
		Assert.IsFalse(tagLine.Contains('>', StringComparison.Ordinal), tagLine);
		Assert.IsFalse(tagLine.Contains('&', StringComparison.Ordinal), tagLine);
	}

	[TestMethod]
	public void Template_HtmlEscaping_DynamicValues()
	{
		// 设计 §3.6：HTML 模式下所有动态值转义（& 先转、再 < >）——banner / 昵称是攻击者可控值，
		// 地区名 / 组织名来自库数据；消息里裸尖括号只可能来自我们自己插入的 code 标签
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Close,
			Host = "198.51.100.9",
			Port = 40000,
			LocalPort = 2222,
			Banner = "SSH-2.0-<b>'&\"",
			TodayCount = 3,
			TimeUtc = local,
			ConnectTimeUtc = local,
			DurationSeconds = 3661.8,
			BytesSent = 12345,
			Geo = GeoLookupResult.Found(GeoResult("中国<", "CN", "广东省>", "深圳市", "Google <b>&", "15169")),
		};

		string text = NotifyMessage.Build(evt, "box<&>", local);

		StringAssert.Contains(text, "🕳 box&lt;&amp;&gt; · SSHTarpitWatch · 连接断开", "昵称转义");
		StringAssert.Contains(text, "客户端：SSH-2.0-&lt;b&gt;'&amp;\"", "banner 里的 <b> 不成为标记、& 变 &amp;");
		StringAssert.Contains(text, "地区：中国&lt; · 广东省&gt; · 深圳市 (CN)", "地区名转义");
		StringAssert.Contains(text, "ISP：Google &lt;b&gt;&amp; (<code>AS15169</code>)", "组织名转义；code 是唯一的标记来源");

		// 「裸 < 只来自我们自己的 code 标签」：去掉 code 标记后，消息里不得再有任何裸尖括号
		string stripped = text
			.Replace("<code>", "", StringComparison.Ordinal)
			.Replace("</code>", "", StringComparison.Ordinal);
		Assert.IsFalse(stripped.Contains('<'), "除 code 标签外不应有裸 <");
		Assert.IsFalse(stripped.Contains('>'), "除 code 标签外不应有裸 >");
	}

	[TestMethod]
	public void Template_TestNotification_EscapesNickname()
	{
		string text = NotifyMessage.BuildTest("box<&>", local);

		StringAssert.Contains(text, "🕳 box&lt;&amp;&gt; · SSHTarpitWatch · 测试通知");
	}

	/// <summary>取出消息里的地区行（逐字符断言的载体）。</summary>
	private static string RegionLine(GeoResult result) => CloseText(result)
		.Split('\n')
		.First(line => line.StartsWith("地区：", StringComparison.Ordinal));

	/// <summary>取出消息里的 ISP 行（无该行返回空串）。</summary>
	private static string IspLine(GeoLookupResult geo) => CloseText(geo)
		.Split('\n')
		.FirstOrDefault(line => line.StartsWith("ISP：", StringComparison.Ordinal)) ?? "";

	private static string IspLine(GeoResult result) => IspLine(GeoLookupResult.Found(result));

	private static string CloseText(GeoLookupResult geo) => NotifyMessage.Build(CloseEvent(geo), "box", local);

	private static string CloseText(GeoResult result) => CloseText(GeoLookupResult.Found(result));

	private static GeoResult GeoResult(string country, string code, string region, string city, string isp, string asn = "") =>
		new(country, code, region, city, isp, asn, "test.mmdb");

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
