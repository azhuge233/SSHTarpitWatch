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
/// </summary>
internal sealed class NotifyEvent
{
	public NotifyEventKind Kind { get; init; }

	public string Host { get; init; } = "";

	public int Port { get; init; }

	public int LocalPort { get; init; }

	/// <summary>客户端身份串；未上报时为空（消息里显示 —）。</summary>
	public string Banner { get; init; } = "";

	public DateTimeOffset TimeUtc { get; init; }

	/// <summary>连接停留时长（close 事件）。</summary>
	public double DurationSeconds { get; init; }

	/// <summary>已发出字节数（close 事件）。</summary>
	public long BytesSent { get; init; }

	/// <summary>该 IP 当日第 N 次连接（connect 事件；设计 §3.2 步 1）。</summary>
	public int TodayCount { get; init; }

	/// <summary>本连接的 connect 是否通过去重闸（close 事件用；设计 §3.2 步 2）。</summary>
	public bool ConnectGatePassed { get; init; }
}
