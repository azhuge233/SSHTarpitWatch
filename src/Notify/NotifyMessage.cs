using System.Globalization;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 消息模板（设计 §3.3）：连接条 / 断开条 / 测试通知。
/// 时间显示服务器本地时间（含 UTC 偏移）；banner 缺失显示 —；昵称空回退系统主机名。
/// </summary>
internal static class NotifyMessage
{
	/// <summary>banner 缺失时的占位（设计 §3.3 / R2.2）。</summary>
	public const string NoBanner = "—";

	/// <summary>按事件类型渲染（localTime 已换算为服务器本地时间）。</summary>
	public static string Build(NotifyEvent evt, string nickname, DateTimeOffset localTime) => evt.Kind switch
	{
		NotifyEventKind.Connect => BuildConnect(evt, nickname, localTime),
		_ => BuildClose(evt, nickname),
	};

	/// <summary>--test-notify 的测试通知（设计 §4.4）：与真实事件模板区分，避免误读为连接事件。</summary>
	public static string BuildTest(string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {nickname} · SSHTarpitWatch · 测试通知\n"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})");

	/// <summary>昵称为空（或全空白）时回退系统主机名（R3.4）。</summary>
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

	private static string BuildConnect(NotifyEvent evt, string nickname, DateTimeOffset localTime) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {nickname} · SSHTarpitWatch · 新连接\n"
		+ $"来源：{evt.Host}:{evt.Port} → 本机 :{evt.LocalPort}\n"
		+ $"客户端：{(evt.Banner.Length == 0 ? NoBanner : evt.Banner)}\n"
		+ $"时间：{localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{FormatOffset(localTime.Offset)})\n"
		+ $"该 IP 今日第 {evt.TodayCount} 次");

	private static string BuildClose(NotifyEvent evt, string nickname) => string.Create(
		CultureInfo.InvariantCulture,
		$"🕳 {nickname} · SSHTarpitWatch · 连接断开\n"
		+ $"来源：{evt.Host}:{evt.Port}\n"
		+ $"停留：{FormatDuration(evt.DurationSeconds)}\n"
		+ $"发出：{evt.BytesSent} 字节");
}
