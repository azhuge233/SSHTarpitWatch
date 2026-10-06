using System.Globalization;

namespace SSHTarpitWatch.Infra;

/// <summary>
/// 全局累计统计（设计 §3.4/§6），跨 SIGHUP 重载保留。
/// 通知计数器由通知管道递增：sent = 发送成功；suppressed = 被任一闸丢弃；failed = 重试耗尽仍失败。
/// </summary>
internal static class Stats
{
	private static long connects;
	private static long elapsedMs;
	private static long bytes;
	private static long notifSent;
	private static long notifSuppressed;
	private static long notifFailed;

	/// <summary>连接数（accept 时计）。</summary>
	public static void CountConnect() => Interlocked.Increment(ref connects);

	/// <summary>收盘累计：停留时长与发出字节。</summary>
	public static void CountClose(double seconds, long sentBytes)
	{
		Interlocked.Add(ref elapsedMs, (long)Math.Round(seconds * 1000));
		Interlocked.Add(ref bytes, sentBytes);
	}

	/// <summary>通知发送成功（管道工作器）。</summary>
	public static void CountNotifySent() => Interlocked.Increment(ref notifSent);

	/// <summary>通知被任一闸压制/丢弃（开关、去重、限速、队满）。</summary>
	public static void CountNotifySuppressed() => Interlocked.Increment(ref notifSuppressed);

	/// <summary>通知重试耗尽仍失败。</summary>
	public static void CountNotifyFailed() => Interlocked.Increment(ref notifFailed);

	/// <summary>TOTALS 行（设计 §6）：seconds 保留 1 位小数。</summary>
	public static string FormatTotals() => string.Create(
		CultureInfo.InvariantCulture,
		$"TOTALS connects={Interlocked.Read(ref connects)} seconds={Interlocked.Read(ref elapsedMs) / 1000.0:F1} "
		+ $"bytes={Interlocked.Read(ref bytes)} notif_sent={Interlocked.Read(ref notifSent)} "
		+ $"notif_suppressed={Interlocked.Read(ref notifSuppressed)} notif_failed={Interlocked.Read(ref notifFailed)}");
}
