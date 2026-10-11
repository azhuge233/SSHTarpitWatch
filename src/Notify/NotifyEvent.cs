using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Notify;

/// <summary>通知事件类型（设计 §3.1）。</summary>
internal enum NotifyEventKind
{
	Connect,
	Close,
}

/// <summary>
/// 通知事件模型（设计 §3.1）：连接（connect）与断开（close）两类事件共用一个模型；
/// 时间由管道在入队时以当前时钟注入（UTC），消息渲染时再换算为服务器本地时间。
/// 断开事件携带连接期快照（连接时刻、本地端口、banner、当日 N 取自快照，设计 §3.1）。
/// </summary>
internal sealed class NotifyEvent
{
	public NotifyEventKind Kind { get; init; }

	public string Host { get; init; } = "";

	public int Port { get; init; }

	/// <summary>本机端口（连接期快照：断开时仍取自连接建立时刻的值）。</summary>
	public int LocalPort { get; init; }

	/// <summary>客户端身份串（连接期快照）；未上报时为空（消息里显示 —）。</summary>
	public string Banner { get; init; } = "";

	/// <summary>事件时刻（UTC）：connect = 连接建立；close = 断开判定——去重闸等以此为「现在」。</summary>
	public DateTimeOffset TimeUtc { get; init; }

	/// <summary>连接建立时刻（UTC）（连接期快照）：两条消息模板的「时间」字段——断开条显示的是连接时刻，而非断开时刻。</summary>
	public DateTimeOffset ConnectTimeUtc { get; init; }

	/// <summary>连接停留时长（close 事件）。</summary>
	public double DurationSeconds { get; init; }

	/// <summary>已发出字节数（close 事件）。</summary>
	public long BytesSent { get; init; }

	/// <summary>该 IP 今日第 N 次连接（连接期快照：步 1 在连接建立时计数，断开条沿用同一个 N；设计 §3.2 步 1）。</summary>
	public int TodayCount { get; init; }

	/// <summary>本连接的 connect 是否通过去重闸（close 事件用；设计 §3.2 步 2）。</summary>
	public bool ConnectGatePassed { get; init; }

	/// <summary>
	/// 地区查询结果（连接期快照：连接时异步查库的结果随快照到断开时刻，设计 §3.1 / §3.6）。
	/// 默认值 = 未启用（NoDatabase）→ 消息里不渲染地区行（与 GeoIP 落地前的行为一致）。
	/// </summary>
	public GeoLookupResult Geo { get; init; }
}
