using System.Threading.Channels;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 发送工作器（设计 §3.2 步 5，单工作器）：从有界队列逐条取事件发送；
/// 失败重试 ≤3 次（退避 2s/10s/30s），429 按其 retry_after 等待后重试（计入重试次数）；
/// 最终失败只记日志 + 计数——通知链路故障绝不影响困住逻辑（N4）。
/// </summary>
internal sealed class NotifyWorker
{
	private static readonly TimeSpan[] retryBackoffs =
		[TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

	private readonly ChannelReader<NotifyEvent> reader;
	private readonly ConfigStore configStore;
	private readonly INotifier notifier;
	private readonly TimeProvider timeProvider;
	private readonly Action onProcessed;

	public NotifyWorker(
		ChannelReader<NotifyEvent> reader,
		ConfigStore configStore,
		INotifier notifier,
		TimeProvider timeProvider,
		Action onProcessed)
	{
		this.reader = reader;
		this.configStore = configStore;
		this.notifier = notifier;
		this.timeProvider = timeProvider;
		this.onProcessed = onProcessed;
	}

	/// <summary>消费循环：直到队列完成（退出排空）或被强制取消（排空超时）。</summary>
	public async Task RunAsync(CancellationToken token)
	{
		try
		{
			await foreach (NotifyEvent evt in reader.ReadAllAsync(token).ConfigureAwait(false))
			{
				try
				{
					await SendWithRetriesAsync(evt, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					return; // 排空超时被强制取消：剩余条数由 dropped_pending 记账
				}
				finally
				{
					onProcessed();
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 强制取消（shutdown 超时截断）
		}
	}

	private async Task SendWithRetriesAsync(NotifyEvent evt, CancellationToken token)
	{
		AppConfig snapshot = configStore.Current;
		string nickname = NotifyMessage.ResolveNickname(snapshot.Nickname);
		DateTimeOffset local = TimeZoneInfo.ConvertTime(evt.TimeUtc, timeProvider.LocalTimeZone);
		string text = NotifyMessage.Build(evt, nickname, local);
		for (int attempt = 0; ; attempt++)
		{
			NotifySendResult result = await TrySendAsync(text, token).ConfigureAwait(false);
			if (result.Success)
			{
				Stats.CountNotifySent();
				Log.Info($"NOTIFY kind={evt.Kind.ToLogString()} host={evt.Host} result=sent");
				return;
			}

			if (attempt >= retryBackoffs.Length)
			{
				Stats.CountNotifyFailed();
				Log.Info($"NOTIFY kind={evt.Kind.ToLogString()} host={evt.Host} "
					+ $"result=failed error=\"{result.Error}\"");
				return;
			}

			TimeSpan wait = result.RetryAfter ?? retryBackoffs[attempt];
			Log.Debug($"NOTIFY retry kind={evt.Kind.ToLogString()} host={evt.Host} "
				+ $"attempt={attempt + 1} wait={wait.TotalSeconds:F0}s");
			await Task.Delay(wait, timeProvider, token).ConfigureAwait(false);
		}
	}

	/// <summary>单次发送：渠道异常一律折算为失败（N4）；仅调用方取消时向上抛。</summary>
	private async Task<NotifySendResult> TrySendAsync(string text, CancellationToken token)
	{
		try
		{
			return await notifier.SendAsync(text, configStore.Current.Notifications, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			return NotifySendResult.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}
}
