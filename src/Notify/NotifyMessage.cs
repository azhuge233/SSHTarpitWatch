using System.Globalization;
using System.Text;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 消息模板（设计 §3.3）：连接条 / 断开条（默认唯一一条，合并连接期信息）/ 测试通知。
/// 「时间」一律取连接建立时刻（close 事件经连接期快照带到断开时刻）；显示服务器本地时间（含 UTC 偏移）；
/// banner 缺失显示 —；昵称空回退系统主机名。
/// 消息按 Telegram HTML 模式发送（设计 §3.6）：所有动态值（昵称 / banner / 地区名 / ISP / ASN / IP / 端口）
/// 一律经 Escape 转义；消息里裸尖括号的唯一来源是我们自己插入的 &lt;code&gt;（标签行不加任何格式标记）。
/// </summary>
internal static class NotifyMessage
{
	/// <summary>banner 缺失时的占位（设计 §3.3 / R2.2）。</summary>
	public const string NoBanner = "—";

	/// <summary>库缺失提示文案（设计 §3.6 / R5.4：enabled=true 但库文件不可读）。</summary>
	public const string GeoMissing = "库缺失";

	/// <summary>无查询结果提示文案（设计 §3.6 / R5.4：库可读但该 IP 无记录）。</summary>
	public const string GeoNoRecord = "无查询结果";

	/// <summary>凑不出任何标签时的标签文案（R3.9：geoip 启用但库缺失 / 无查询结果 / 结果里没有可成标签的值）。</summary>
	public const string GeoUnknownTag = "未查到信息";

	/// <summary>按事件类型渲染（localTime = 事件连接时刻的服务器本地换算）。</summary>
	public static string Build(NotifyEvent evt, string nickname, DateTimeOffset localTime) => evt.Kind switch
	{
		NotifyEventKind.Connect => BuildConnect(evt, nickname, localTime),
		_ => BuildClose(evt, nickname, localTime),
	};

	/// <summary>--test-notify 的测试通知（设计 §4.4）：与真实事件模板区分，避免误读为连接事件。</summary>
	public static string BuildTest(string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {Escape(nickname)} · SSHTarpitWatch · 测试通知\n"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})");

	/// <summary>昵称为空（或全空白）时回退系统主机名（R3.5）。</summary>
	public static string ResolveNickname(string configured) =>
		string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured;

	/// <summary>时长格式：X 小时 Y 分 Z 秒（秒向下取整）。</summary>
	public static string FormatDuration(double totalSeconds)
	{
		long seconds = (long)Math.Max(0, totalSeconds);
		return string.Create(
			CultureInfo.InvariantCulture,
			$"{seconds / 3600} 小时 {seconds % 3600 / 60} 分 {seconds % 60} 秒");
	}

	/// <summary>UTC 偏移格式 ±hh:mm（如 +08:00 / -05:00 / +00:00）。</summary>
	public static string FormatOffset(TimeSpan offset)
	{
		char sign = offset < TimeSpan.Zero ? '-' : '+';
		TimeSpan abs = offset.Duration();
		return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours:D2}:{abs.Minutes:D2}");
	}

	/// <summary>连接条（可选：仅 filter.on_connect=true 时发出；模板不变）。</summary>
	private static string BuildConnect(NotifyEvent evt, string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {Escape(nickname)} · SSHTarpitWatch · 新连接\n"
		+ $"来源：{Escape(evt.Host)}:{Escape(evt.Port.ToString(CultureInfo.InvariantCulture))} → 本机 :{Escape(evt.LocalPort.ToString(CultureInfo.InvariantCulture))}\n"
		+ $"客户端：{FormatBanner(evt.Banner)}\n"
		+ $"{FormatGeoLines(evt.Geo)}"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})\n"
		+ $"该 IP 今日第 {evt.TodayCount} 次{FormatTagLine(evt.Geo)}");

	/// <summary>断开条（默认唯一一条，与连接期信息合并——来源/本地端口/客户端/连接时刻/停留/字节/当日 N）。</summary>
	private static string BuildClose(NotifyEvent evt, string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {Escape(nickname)} · SSHTarpitWatch · 连接断开\n"
		+ $"来源：{Escape(evt.Host)}:{Escape(evt.Port.ToString(CultureInfo.InvariantCulture))} → 本机 :{Escape(evt.LocalPort.ToString(CultureInfo.InvariantCulture))}\n"
		+ $"客户端：{FormatBanner(evt.Banner)}\n"
		+ $"{FormatGeoLines(evt.Geo)}"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})\n"
		+ $"停留：{FormatDuration(evt.DurationSeconds)}\n"
		+ $"发出：{evt.BytesSent} 字节\n"
		+ $"该 IP 今日第 {evt.TodayCount} 次{FormatTagLine(evt.Geo)}");

	/// <summary>
	/// 标签行（R3.9，2026-10-12 修订）：Telegram 通知自动追加、无配置键；地区 tag（原规则）+
	/// ISP tag：`#AS{号}` 与 `#组织名`（净化后，号在前）。
	/// geoip 未启用（NoDatabase）→ 整行不出现；凑不出任何标签才 `#未查到信息`（只有 ISP/ASN 结果 → 出 ISP tag）；
	/// 标签不加任何格式标记（否则 Telegram 不识别为可点标签）。返回带前导换行的整行（无内容返回空串）。
	/// </summary>
	private static string FormatTagLine(GeoLookupResult geo)
	{
		if (geo.Status == GeoStatus.NoDatabase)
		{
			return "";
		}

		if (geo.Status != GeoStatus.Ok || geo.Result is not GeoResult result)
		{
			return $"\n#{GeoUnknownTag}";
		}

		List<string> tags = [];
		foreach (string value in new[] { result.Country, result.CountryCode, result.Region, result.City })
		{
			string tag = ToHashtag(value);
			if (tag.Length > 0)
			{
				tags.Add($"#{tag}");
			}
		}

		if (result.Asn.Length > 0)
		{
			tags.Add($"#AS{Escape(result.Asn)}");
		}

		string ispTag = ToHashtag(result.Isp);
		if (ispTag.Length > 0)
		{
			tags.Add($"#{ispTag}");
		}

		return tags.Count == 0 ? $"\n#{GeoUnknownTag}" : $"\n{string.Join(" ", tags)}";
	}

	/// <summary>
	/// Telegram 标签净化：仅保留字母 / 数字（其余替换为 `_`）、连续下划线合并为一个、去首尾下划线
	/// （`Cloudflare, Inc.` → `Cloudflare_Inc`）；全非法则返回空串。
	/// </summary>
	private static string ToHashtag(string value)
	{
		StringBuilder builder = new(value.Length);
		foreach (char ch in value)
		{
			if (char.IsLetterOrDigit(ch))
			{
				builder.Append(ch);
			}
			else if (builder.Length > 0 && builder[^1] != '_')
			{
				builder.Append('_');
			}
		}

		return builder.ToString().Trim('_');
	}

	/// <summary>banner 缺失显示占位；非空 banner 转义后进消息（R2.2 / 攻击者可控值）。</summary>
	private static string FormatBanner(string banner) => banner.Length == 0 ? NoBanner : Escape(banner);

	/// <summary>
	/// 地区行 + ISP 行（含行尾换行；无内容返回空串）——设计 §3.3 模板 / §3.6：
	/// enabled=false 整行不出现；库缺失 / 无查询结果为提示行；ISP 行按「名 + 号」组合出现（只有其一则只显示其一）。
	/// </summary>
	private static string FormatGeoLines(GeoLookupResult geo)
	{
		string[] lines = [FormatRegionLine(geo), FormatIspLine(geo)];
		string content = string.Join("\n", lines.Where(line => line.Length > 0));
		return content.Length == 0 ? "" : content + "\n";
	}

	/// <summary>地区行：地区字段全空（只有 ISP/ASN 的库）→ 本行不出现。</summary>
	private static string FormatRegionLine(GeoLookupResult geo) => geo.Status switch
	{
		GeoStatus.DbMissing => $"地区：{GeoMissing}",
		GeoStatus.NoRecord => $"地区：{GeoNoRecord}",
		GeoStatus.Ok when geo.Result is GeoResult result => FormatRegionText(result),
		_ => "",
	};

	/// <summary>地区文案：逐级省略空值；名称全空但码非空 → 只显示码；名称 + 码 → 名称后跟 (码)；动态值转义。</summary>
	private static string FormatRegionText(GeoResult result)
	{
		string names = JoinNames(result.Country, result.Region, result.City);
		string code = Escape(result.CountryCode);
		if (names.Length == 0 && code.Length == 0)
		{
			return "";
		}

		string text = names.Length == 0 ? code : code.Length == 0 ? names : $"{names} ({code})";
		return $"地区：{text}";
	}

	/// <summary>
	/// ISP 行（设计 §3.3 / R5.3）：名 + 号 → `ISP：{组织名} (&lt;code&gt;AS{号}&lt;/code&gt;)`；只有名 → `ISP：{组织名}`；
	/// 只有号 → `ISP：&lt;code&gt;AS{号}&lt;/code&gt;`；都没有 / 非 Ok → 整行不出现。号包 code 标签供 Telegram 点按复制。
	/// </summary>
	private static string FormatIspLine(GeoLookupResult geo)
	{
		if (geo.Status != GeoStatus.Ok || geo.Result is not GeoResult result)
		{
			return "";
		}

		string name = Escape(result.Isp);
		string number = Escape(result.Asn);
		if (name.Length > 0 && number.Length > 0)
		{
			return $"ISP：{name} (<code>AS{number}</code>)";
		}

		if (name.Length > 0)
		{
			return $"ISP：{name}";
		}

		return number.Length > 0 ? $"ISP：<code>AS{number}</code>" : "";
	}

	private static string JoinNames(string country, string region, string city) =>
		string.Join(" · ", new[] { country, region, city }.Where(name => name.Length > 0).Select(Escape));

	/// <summary>
	/// Telegram HTML 模式转义（设计 §3.6）：`&amp;` 先转（后续 &amp;lt; / &amp;gt; 不再被二次转义），再 `&lt;` / `&gt;`。
	/// 消息里所有动态值一律经此处理——裸尖括号只可能来自我们自己插入的 code 标签。
	/// </summary>
	private static string Escape(string value) => value
		.Replace("&", "&amp;", StringComparison.Ordinal)
		.Replace("<", "&lt;", StringComparison.Ordinal)
		.Replace(">", "&gt;", StringComparison.Ordinal);
}
