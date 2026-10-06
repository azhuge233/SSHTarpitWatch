using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 通知渠道接口（设计 §3.1/§10 扩展点）：v1 唯一实现是 <see cref="TelegramNotifier"/>；
/// 新增渠道（Webhook 等）只需实现本接口 + 在 notifications 下新增同级配置段。
/// </summary>
internal interface INotifier
{
	/// <summary>当前配置快照下本渠道是否启用（管道步 0 的渠道闸，设计 §3.2）。</summary>
	bool IsEnabled(NotificationConfig config);

	/// <summary>
	/// 发送一条已渲染的通知文本；失败以返回值表达（超时/429/网络错误等），不抛异常；
	/// 仅当调用方取消（<paramref name="cancellationToken"/>）时抛 OperationCanceledException。
	/// </summary>
	Task<NotifySendResult> SendAsync(string text, NotificationConfig config, CancellationToken cancellationToken);
}
