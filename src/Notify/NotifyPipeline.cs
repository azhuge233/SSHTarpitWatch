using System.Threading.Channels;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 通知管道（设计 §3.2，步骤 0..6 顺序固定）：
/// 0 渠道/开关闸 + 时长闸 → 1 当日计数 → 2 去重闸 → 3 全局限速 → 4 有界队列（1024，丢新）→ 5 单工作器发送 → 6 退出排空（≤5s）。
/// 入队即返回：主路径永不等待网络，通知链路故障绝不影响困住逻辑（§3.2 步 7 / N4）。
/// </summary>
internal sealed class NotifyPipeline : IAsyncDisposable
{
	/// <summary>有界队列容量（设计 §3.2 步 4）。</summary>
	public const int QueueCapacity = 1024;

	/// <summary>退出排空上限（设计 §3.2 步 6 / §5）。</summary>
	public static readonly TimeSpan DrainQueueTimeout = TimeSpan.FromSeconds(5);

	private static readonly TimeSpan cleanupInterval = TimeSpan.FromMinutes(10);

	private readonly ConfigStore configStore;
	private readonly INotifier notifier;
	private readonly TimeProvider timeProvider;
	private readonly NotifyLedger ledger;
	private readonly TokenBucket rateLimit;
	private readonly Channel<NotifyEvent> queue;
	private readonly CancellationTokenSource workerCts = new();
	private readonly ITimer cleanupTimer;
	private readonly Task worker;
	private long pending;
	private int shutdownState;

	public NotifyPipeline(ConfigStore configStore, INotifier notifier, TimeProvider? timeProvider = null)
	{
		this.configStore = configStore;
		this.notifier = notifier;
		this.timeProvider = timeProvider ?? TimeProvider.System;
		ledger = new NotifyLedger();
		rateLimit = new TokenBucket(this.timeProvider);
		queue = Channel.CreateBounded<NotifyEvent>(new BoundedChannelOptions(QueueCapacity)
		{
			// FullMode=Wait：TryWrite 在队满时返回 false → 丢新 + 计数（设计 §3.2 步 4）
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false,
		});
		cleanupTimer = this.timeProvider.CreateTimer(_ => RunCleanup(), null, cleanupInterval, cleanupInterval);
		NotifyWorker sender = new(queue.Reader, configStore, notifier, this.timeProvider,
			() => Interlocked.Decrement(ref pending));
		worker = Task.Run(() => sender.RunAsync(workerCts.Token));
	}

	/// <summary>已入队但尚未完成发送的条数（含在途一条）。</summary>
	public long PendingCount => Interlocked.Read(ref pending);

	/// <summary>
	/// 连接事件入队（设计 §2 步 5：首行读取结束/超时后触发）。
	/// 步 1（当日计数）始终执行——被开关压制或去重丢弃的连接同样计入 N。
	/// 返回的连接期快照（banner / 本地端口 / 连接时刻 / 当日 N / 过闸标记）由调用方随连接保留到断开（设计 §3.1/§3.4）。
	/// </summary>
	public ConnectEnqueueResult EnqueueConnect(string host, int port, int localPort, string banner)
	{
		int todayCount = ledger.RegisterConnect(host, timeProvider.GetLocalNow());
		DateTimeOffset nowUtc = timeProvider.GetUtcNow();
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Connect,
			Host = host,
			Port = port,
			LocalPort = localPort,
			Banner = banner,
			TimeUtc = nowUtc,
			ConnectTimeUtc = nowUtc,
			TodayCount = todayCount,
		};
		NotifyEnqueueResult result = Process(evt, configStore.Current.Notifications);
		ConnectionSnapshot snapshot = new(host, port, localPort, banner, nowUtc, todayCount, result.GatePassed);
		return new ConnectEnqueueResult(result, snapshot);
	}

	/// <summary>
	/// 断开事件入队（设计 §2 步 7 / §3.2）：以连接期快照渲染合并模板（§3.3）——
	/// 来源/本地端口/客户端/连接时刻/当日 N 取自快照；去重闸以断开判定时刻为「现在」。
	/// </summary>
	public NotifyEnqueueResult EnqueueClose(ConnectionSnapshot snapshot, double durationSeconds, long bytesSent)
	{
		NotifyEvent evt = new()
		{
			Kind = NotifyEventKind.Close,
			Host = snapshot.Host,
			Port = snapshot.Port,
			LocalPort = snapshot.LocalPort,
			Banner = snapshot.Banner,
			TimeUtc = timeProvider.GetUtcNow(),
			ConnectTimeUtc = snapshot.ConnectTimeUtc,
			TodayCount = snapshot.TodayCount,
			ConnectGatePassed = snapshot.GatePassed,
			DurationSeconds = durationSeconds,
			BytesSent = bytesSent,
		};
		return Process(evt, configStore.Current.Notifications);
	}

	/// <summary>退出排空（设计 §3.2 步 6）：停止入队 → 最多 drainTimeout → 超时记 dropped_pending 后强制收尾。</summary>
	public Task ShutdownAsync(TimeSpan drainTimeout)
	{
		if (Interlocked.Exchange(ref shutdownState, 1) != 0)
		{
			return Task.CompletedTask;
		}

		return DrainAsync(drainTimeout);
	}

	public ValueTask DisposeAsync() => new(ShutdownAsync(DrainQueueTimeout));

	/// <summary>步骤 0..4，顺序固定（设计 §3.2）。</summary>
	private NotifyEnqueueResult Process(NotifyEvent evt, NotificationConfig notifications)
	{
		// 步 0：渠道/开关闸
		if (!notifier.IsEnabled(notifications) || !KindSwitchOn(evt.Kind, notifications))
		{
			return Suppress(evt, NotifyResult.SuppressedSwitch, gatePassed: false);
		}

		// 步 0：时长闸（仅 close，设计 §3.2 / R3.4）——停留 < 阈值 → 丢弃 + 计数；
		// 不写去重账本（被过滤的连接不占用该 IP 冷却额度），本步先于步 2 的去重闸
		if (IsBelowMinDuration(evt, notifications))
		{
			return Suppress(evt, NotifyResult.SuppressedDuration, gatePassed: false);
		}

		// 步 2：去重闸（通过即写账本）
		if (!PassesDedup(evt, notifications))
		{
			return Suppress(evt, NotifyResult.SuppressedDedup, gatePassed: false);
		}

		// 步 3：全局限速（过闸标记不因限速丢弃回滚）
		if (!rateLimit.TryConsume(notifications.Throttle.MaxPerMinute))
		{
			return Suppress(evt, NotifyResult.SuppressedRate, gatePassed: true);
		}

		// 步 4：有界队列（队满丢新；过闸标记不回滚）
		// 先计数再入队：消除“工作器先完成后递减”与“入队后递增”之间的计数窗口（PendingCount/dropped_pending 要准）
		Interlocked.Increment(ref pending);
		if (!queue.Writer.TryWrite(evt))
		{
			Interlocked.Decrement(ref pending);
			return Suppress(evt, NotifyResult.SuppressedQueue, gatePassed: true);
		}

		LogNotify(evt, NotifyResult.Queued);
		return new NotifyEnqueueResult(NotifyResult.Queued, true);
	}

	private static bool KindSwitchOn(NotifyEventKind kind, NotificationConfig notifications) =>
		kind == NotifyEventKind.Connect ? notifications.Filter.OnConnect : notifications.Filter.OnClose;

	/// <summary>时长闸（设计 §3.2 步 0）：仅断开条；阈值 &gt; 0 且停留 &lt; 阈值 → 压制（≥ 语义：等于阈值要通知）。</summary>
	private static bool IsBelowMinDuration(NotifyEvent evt, NotificationConfig notifications) =>
		evt.Kind == NotifyEventKind.Close
		&& notifications.Filter.MinDurationSeconds > 0
		&& evt.DurationSeconds < notifications.Filter.MinDurationSeconds;

	private bool PassesDedup(NotifyEvent evt, NotificationConfig notifications)
	{
		if (evt.Kind == NotifyEventKind.Connect)
		{
			return ledger.TryPassDedup(evt.Host, evt.TimeUtc, notifications.Throttle.PerIpSeconds);
		}

		if (notifications.Filter.OnConnect)
		{
			// 断开条跟随其连接条（R3.6）：connect 未过闸 → 断开也不过闸；过闸则刷新账本
			if (!evt.ConnectGatePassed)
			{
				return false;
			}

			ledger.MarkPassed(evt.Host, evt.TimeUtc);
			return true;
		}

		// on_connect=false（默认）：按与 connect 相同的每 IP 冷却独立判定
		return ledger.TryPassDedup(evt.Host, evt.TimeUtc, notifications.Throttle.PerIpSeconds);
	}

	private static NotifyEnqueueResult Suppress(NotifyEvent evt, NotifyResult result, bool gatePassed)
	{
		Stats.CountNotifySuppressed();
		LogNotify(evt, result);
		return new NotifyEnqueueResult(result, gatePassed);
	}

	private static void LogNotify(NotifyEvent evt, NotifyResult result) =>
		Log.Info($"NOTIFY kind={evt.Kind.ToLogString()} host={evt.Host} result={result.ToLogString()}");

	/// <summary>清理定时器回调（设计 §3.4）：任何异常只记日志，绝不逃逸到线程池。</summary>
	private void RunCleanup()
	{
		try
		{
			AppConfig config = configStore.Current;
			ledger.Cleanup(timeProvider.GetUtcNow(), timeProvider.GetLocalNow(), config.Notifications.Throttle.PerIpSeconds);
		}
		catch (Exception ex)
		{
			Log.Error($"NOTIFY cleanup failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private async Task DrainAsync(TimeSpan drainTimeout)
	{
		cleanupTimer.Dispose();
		queue.Writer.TryComplete();
		try
		{
			if (await WaitForWorkerAsync(drainTimeout).ConfigureAwait(false))
			{
				await worker.ConfigureAwait(false);
				return;
			}

			Log.Info($"NOTIFY dropped_pending={PendingCount}");
			await workerCts.CancelAsync().ConfigureAwait(false);
			await IgnoreWorkerFailureAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Log.Error($"NOTIFY worker error: {ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>等待工作器收尾直到超时（用注入时钟，假时钟单测可确定地驱动排空超时）。</summary>
	private async Task<bool> WaitForWorkerAsync(TimeSpan timeout)
	{
		if (worker.IsCompleted)
		{
			return true;
		}

		using CancellationTokenSource timeoutCts = new();
		Task delay = Task.Delay(timeout, timeProvider, timeoutCts.Token);
		bool workerFinished = await Task.WhenAny(worker, delay).ConfigureAwait(false) == worker;
		await timeoutCts.CancelAsync().ConfigureAwait(false); // 工作器先完成时释放剩余等待定时器
		return workerFinished;
	}

	private async Task IgnoreWorkerFailureAsync()
	{
		try
		{
			await worker.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Log.Debug($"NOTIFY worker stop: {ex.GetType().Name}: {ex.Message}");
		}
	}
}
