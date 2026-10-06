namespace SSHTarpitWatch.Notify;

/// <summary>通知事件在管道中的处置结果（设计 §3.2 / §6 的 result 取值）。</summary>
internal enum NotifyResult
{
	/// <summary>已入队，等待发送。</summary>
	Queued,

	/// <summary>发送成功（工作器）。</summary>
	Sent,

	/// <summary>重试耗尽仍失败（工作器）。</summary>
	Failed,

	/// <summary>步 0：渠道或该 kind 开关关闭。</summary>
	SuppressedSwitch,

	/// <summary>步 2：去重闸（每 IP 冷却）。</summary>
	SuppressedDedup,

	/// <summary>步 3：全局限速。</summary>
	SuppressedRate,

	/// <summary>步 4：队满丢新（§6 示意列表之外的补充取值，便于观测内存保护触发）。</summary>
	SuppressedQueue,
}

internal static class NotifyResults
{
	/// <summary>NOTIFY 日志行的 result 字段（设计 §6）。</summary>
	public static string ToLogString(this NotifyResult result) => result switch
	{
		NotifyResult.Queued => "queued",
		NotifyResult.Sent => "sent",
		NotifyResult.Failed => "failed",
		NotifyResult.SuppressedSwitch => "suppressed_switch",
		NotifyResult.SuppressedDedup => "suppressed_dedup",
		NotifyResult.SuppressedRate => "suppressed_rate",
		_ => "suppressed_queue",
	};

	/// <summary>kind 字段。</summary>
	public static string ToLogString(this NotifyEventKind kind) =>
		kind == NotifyEventKind.Connect ? "connect" : "close";
}

/// <summary>
/// 入队判定结果：Result = 处置；GatePassed = 该事件的去重闸是否通过——
/// <b>不因后续限速丢弃、队满或发送失败而回滚</b>（设计 §3.2 步 2）。
/// </summary>
internal readonly record struct NotifyEnqueueResult(NotifyResult Result, bool GatePassed);

/// <summary>
/// 单次发送结果：失败以返回值表达（渠道异常不向调用方抛出，取消除外）。
/// RetryAfter 非空 = 渠道明确告知的重试等待（Telegram 429，设计 §3.2 步 5）。
/// </summary>
internal readonly record struct NotifySendResult(bool Success, TimeSpan? RetryAfter, string Error)
{
	public static NotifySendResult Ok() => new(true, null, "");

	public static NotifySendResult Fail(string error, TimeSpan? retryAfter = null) =>
		new(false, retryAfter, error);
}
