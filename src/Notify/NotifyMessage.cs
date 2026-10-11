using System.Globalization;
using System.Text;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 消息模板（设计 §3.3）：连接条 / 断开条（默认唯一一条，合并连接期信息）/ 测试通知。
/// 「时间」一律取连接建立时刻（close 事件经连接期快照带到断开时刻）；显示服务器本地时间（含 UTC 偏移）；
/// banner 缺失显示 —；昵称空回退系统主机名。
/// </summary>
internal static class NotifyMessage
{
	/// <summary>banner 缺失时的占位（设计 §3.3 / R2.2）。</summary>
	public const string NoBanner = "—";

	/// <summary>库缺失提示文案（设计 §3.6 / R5.4：enabled=true 但库文件不可读）。</summary>
	public const string GeoMissing = "库缺失";

	/// <summary>无查询结果提示文案（设计 §3.6 / R5.4：库可读但该 IP 无记录）。</summary>
	public const string GeoNoRecord = "无查询结果";

	/// <summary>无地区信息时的标签文案（R3.9：geoip 启用但库缺失 / 无查询结果）。</summary>
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
		$"🕳 {nickname} · SSHTarpitWatch · 测试通知\n"
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
		$"🕳 {nickname} · SSHTarpitWatch · 新连接\n"
		+ $"来源：{evt.Host}:{evt.Port} → 本机 :{evt.LocalPort}\n"
		+ $"客户端：{FormatBanner(evt.Banner)}\n"
		+ $"{FormatGeoLines(evt.Geo)}"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})\n"
		+ $"该 IP 今日第 {evt.TodayCount} 次{FormatTagLine(evt.Geo)}");

	/// <summary>断开条（默认唯一一条，与连接期信息合并——来源/本地端口/客户端/连接时刻/停留/字节/当日 N）。</summary>
	private static string BuildClose(NotifyEvent evt, string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {nickname} · SSHTarpitWatch · 连接断开\n"
		+ $"来源：{evt.Host}:{evt.Port} → 本机 :{evt.LocalPort}\n"
		+ $"客户端：{FormatBanner(evt.Banner)}\n"
		+ $"{FormatGeoLines(evt.Geo)}"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})\n"
		+ $"停留：{FormatDuration(evt.DurationSeconds)}\n"
		+ $"发出：{evt.BytesSent} 字节\n"
		+ $"该 IP 今日第 {evt.TodayCount} 次{FormatTagLine(evt.Geo)}");

	/// <summary>
	/// 标签行（R3.9，2026-10-11 定稿）：Telegram 通知自动追加、无配置键；仅地区 tag（不设 IP tag）。
	/// geoip 未启用（NoDatabase）→ 整行不出现；启用但无地区信息（库缺失 / 无查询结果）→ `#未查到信息`；
	/// 查到结果 → `#国家名 #国家码 #省 #市`（有哪级出哪级；非字母/数字/下划线字符替换为 `_`）。
	/// 返回带前导换行的整行（无内容返回空串）。
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

		return tags.Count == 0 ? $"\n#{GeoUnknownTag}" : $"\n{string.Join(" ", tags)}";
	}

	/// <summary>Telegram 标签净化：仅保留字母 / 数字 / 下划线（其余替换为 `_`），去首尾下划线；全非法则返回空串。</summary>
	private static string ToHashtag(string value)
	{
		StringBuilder builder = new(value.Length);
		foreach (char ch in value)
		{
			builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
		}

		return builder.ToString().Trim('_');
	}

	private static string FormatBanner(string banner) => banner.Length == 0 ? NoBanner : banner;

	/// <summary>
	/// 地区行 + ISP 行（含行尾换行；无内容返回空串）——设计 §3.3 模板 / §3.6：
	/// enabled=false 整行不出现；库缺失 / 无查询结果为提示行；ISP 行仅当库给了运营商名时出现。
	/// </summary>
	private static string FormatGeoLines(GeoLookupResult geo)
	{
		string[] lines = [FormatRegionLine(geo), FormatIspLine(geo)];
		string content = string.Join("\n", lines.Where(line => line.Length > 0));
		return content.Length == 0 ? "" : content + "\n";
	}

	/// <summary>地区行：地区字段全空（只有 ISP 的库）→ 本行不出现。</summary>
	private static string FormatRegionLine(GeoLookupResult geo) => geo.Status switch
	{
		GeoStatus.DbMissing => $"地区：{GeoMissing}",
		GeoStatus.NoRecord => $"地区：{GeoNoRecord}",
		GeoStatus.Ok when geo.Result is GeoResult result => FormatRegionText(result),
		_ => "",
	};

	/// <summary>地区文案：逐级省略空值；名称全空但码非空 → 只显示码；名称 + 码 → 名称后跟 (码)。</summary>
	private static string FormatRegionText(GeoResult result)
	{
		string names = JoinNames(result.Country, result.Region, result.City);
		if (names.Length == 0 && result.CountryCode.Length == 0)
		{
			return "";
		}

		string text = names.Length == 0
			? result.CountryCode
			: result.CountryCode.Length == 0 ? names : $"{names} ({result.CountryCode})";
		return $"地区：{text}";
	}

	/// <summary>ISP 行：仅当查到结果且运营商名非空时出现（设计 §3.3）。</summary>
	private static string FormatIspLine(GeoLookupResult geo) =>
		geo.Status == GeoStatus.Ok && geo.Result?.Isp.Length > 0 ? $"ISP：{geo.Result.Isp}" : "";

	private static string JoinNames(string country, string region, string city) =>
		string.Join(" · ", new[] { country, region, city }.Where(name => name.Length > 0));
}
