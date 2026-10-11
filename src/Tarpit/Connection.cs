using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Geo;
using SSHTarpitWatch.Infra;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tarpit;

/// <summary>
/// 单条连接（设计 §2）：读客户端首行（与慢滴并发、不阻塞发送）+ 按 accept 锚点慢滴随机行；
/// 首行读取结束后并发 256B 待读感知对端关闭（FIN / 读错即结束循环，检测粒度不再等于慢滴间隔，设计 §2 步 6）；
/// 首行读取结束/超时后触发连接通知（步 5）并保留连接期快照（banner/本地端口/连接时刻/当日 N，设计 §3.1），
/// 收盘记 CLOSE 并入队断开通知（合并模板用该快照）；SO_RCVBUF=1 为 best effort。
/// </summary>
internal sealed class Connection
{
	/// <summary>待读缓冲（设计 §2 步 6 / §3.4）：256B/连接，首行之后分配并复用；读到的数据一律丢弃。</summary>
	private const int peerReadBufferBytes = 256;

	private readonly Socket socket;
	private readonly ConfigStore configStore;
	private readonly NotifyPipeline notify;
	private readonly GeoLookup geoLookup;
	private readonly CancellationTokenSource closeCts = new();
	private readonly long acceptedTicks;
	private long bytesSent;
	private string banner = "";
	private GeoLookupResult geo;
	private Task connectNotifyTask = Task.CompletedTask;

	/// <summary>停留时长的结束点（单调时钟）= 慢滴循环被打断的瞬间（设计 §2 步 6）；0 = 尚未打断。</summary>
	private long stayEndTicks;

	/// <summary>连接期快照（设计 §3.1/§3.4）：banner/本地端口/连接时刻/当日 N 随连接保留到断开，随连接对象释放而回收。</summary>
	private ConnectionSnapshot? connectSnapshot;

	public Connection(Socket socket, ConfigStore configStore, NotifyPipeline notify, GeoLookup geoLookup)
	{
		this.socket = socket;
		this.configStore = configStore;
		this.notify = notify;
		this.geoLookup = geoLookup;
		acceptedTicks = Stopwatch.GetTimestamp();
		(Host, RemotePort, LocalPort) = DescribeEndpoints(socket);
	}

	public string Host { get; }

	public int RemotePort { get; }

	public int LocalPort { get; }

	/// <summary>客户端首行里的可打印身份串；未上报则为空（通知里显示 —）。</summary>
	public string Banner => banner;

	/// <summary>已发出的字节数（线上字节，含 CRLF）。</summary>
	public long BytesSent => Interlocked.Read(ref bytesSent);

	/// <summary>连接任务；Start 之后可用（退出时等待它收官）。</summary>
	public Task Completion { get; private set; } = Task.CompletedTask;

	/// <summary>启动慢滴与首行读取（两者并发）。</summary>
	public void Start(CancellationToken stopToken)
	{
		ApplyReceiveBufferLimit();
		Completion = Task.Run(() => RunAsync(stopToken));
	}

	/// <summary>外部收盘（优雅退出路径）：取消计时并关闭套接字，收盘日志由连接自身补记。</summary>
	public void RequestClose()
	{
		closeCts.Cancel();
		try
		{
			socket.Close();
		}
		catch (ObjectDisposedException)
		{
			// 连接已自行收盘（套接字已释放）：无需再关
		}
	}

	private async Task RunAsync(CancellationToken stopToken)
	{
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(closeCts.Token, stopToken);
		Task<string> bannerTask = BannerReader.ReadAsync(socket, Host, linked.Token);
		connectNotifyTask = Task.Run(() => FireConnectNotifyAsync(bannerTask));
		Task peerCloseTask = WatchPeerCloseAsync(bannerTask, linked.Token);
		try
		{
			await DripAsync(peerCloseTask, linked.Token).ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
			Log.Debug($"DRAIN host={Host} port={RemotePort} ended={ex.GetType().Name}");
		}
		catch (Exception ex)
		{
			Log.Error($"DRAIN host={Host} port={RemotePort} error={ex.GetType().Name}: {ex.Message}");
		}
		finally
		{
			try
			{
				await linked.CancelAsync().ConfigureAwait(false);
				await AwaitConnectNotifyAsync().ConfigureAwait(false);
				await AwaitPeerWatchAsync(peerCloseTask).ConfigureAwait(false);
				LogClose();
			}
			finally
			{
				socket.Dispose();
			}
		}
	}

	/// <summary>设计 §2 步 5：首行读取结束/超时后触发连接通知；地区查询与读 banner 同时机启动（设计 §3.6），
	/// 结果连同快照保留到断开（banner/本地端口/连接时刻/当日 N/过闸标记/地区结果，设计 §3.1）。</summary>
	private async Task FireConnectNotifyAsync(Task<string> bannerTask)
	{
		ValueTask<GeoLookupResult> geoTask;
		try
		{
			// 门面同步抛错（调度失败等）不能作废整个连接通知任务：banner 采集与入队照常进行（评审 B2-F3）
			geoTask = geoLookup.LookupAsync(Host);
		}
		catch (Exception ex)
		{
			Log.Debug($"GEO lookup host={Host} failed: {ex.GetType().Name}");
			geoTask = ValueTask.FromResult(GeoLookupResult.NoRecord);
		}

		banner = await CaptureBannerAsync(bannerTask).ConfigureAwait(false);
		try
		{
			geo = await geoTask.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// 查库异常就地消化（GeoLookup 内部已兜底，这里再兜一层）：不阻塞慢滴、不影响困住逻辑
			Log.Debug($"GEO lookup host={Host} failed: {ex.GetType().Name}");
			geo = GeoLookupResult.NoRecord;
		}

		try
		{
			connectSnapshot = notify.EnqueueConnect(Host, RemotePort, LocalPort, banner, geo).Snapshot;
		}
		catch (Exception ex)
		{
			// N4：通知链路故障绝不影响困住逻辑
			Log.Error($"NOTIFY enqueue_error kind=connect host={Host} error={ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>
	/// 确保 connect 事件先于 close 事件入队（首行读取可能在收盘时才被取消收尾）。
	/// 异常一律就地消化（只记日志）：这里的失败绝不允许跳过收盘收尾（CLOSE 日志 / 断开通知 / 套接字释放）——
	/// 通知链路故障绝不影响困住逻辑（N4）。
	/// </summary>
	private async Task AwaitConnectNotifyAsync()
	{
		try
		{
			await connectNotifyTask.ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
			// 正常的收盘噪音（对端断开 / 取消 / 套接字已释放）：无需记录
		}
		catch (Exception ex)
		{
			Log.Error($"NOTIFY connect task faulted: {ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>
	/// 设计 §2 步 6：首行读取结束后开始的待读（256B 复用缓冲）——返回 0（FIN）或读异常即对端已离开，
	/// 任务结束即“对端已离开”信号；读到的数据一律丢弃并继续待读。
	/// 自身取消（RequestClose / 停止信号 / 正常收尾）静默结束，不算对端离开。
	/// </summary>
	private async Task WatchPeerCloseAsync(Task<string> bannerTask, CancellationToken token)
	{
		try
		{
			await bannerTask.ConfigureAwait(false);
		}
		catch (Exception)
		{
			// 首行读取的成败由连接通知链路处置（CaptureBannerAsync / AwaitConnectNotifyAsync）：
			// 这里同样只等“首行阶段结束”，无论结果都进入待读——不抢 banner 字节。
		}

		byte[] buffer = new byte[peerReadBufferBytes];
		try
		{
			while (true)
			{
				int received = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
				if (received <= 0)
				{
					Log.Debug($"PEER host={Host} port={RemotePort} closed=fin");
					return;
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 自身取消：正常收盘噪音，不算对端已离开
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
			// 自身取消登记与 socket.Close() 存在竞态：令牌已取消时读错属自我收盘，静默结束（不当对端离开记录）
			if (!token.IsCancellationRequested)
			{
				Log.Debug($"PEER host={Host} port={RemotePort} closed={ex.GetType().Name}");
			}
		}
	}

	/// <summary>
	/// 等待待读任务退出（设计 §2 步 6）：取消先于等待（收尾已取消 linked token），ReceiveAsync 即刻中断——
	/// 保证关停不被待读吊住、收盘后不留悬挂的接收；故障只记日志，绝不影响收盘收尾（与 N4 同款口径）。
	/// </summary>
	private async Task AwaitPeerWatchAsync(Task peerCloseTask)
	{
		try
		{
			await peerCloseTask.ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
		}
		catch (Exception ex)
		{
			Log.Error($"PEER watch faulted: {ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>
	/// 慢滴循环（设计 §2 步 6）：首行于 accept + delay_ms 发出，此后每 delay_ms 一行（单调时钟锚定 accept 时刻）。
	/// 每轮取当前配置快照，热重载即刻生效；对端先在待读上离开即结束循环，写失败照旧抛出（由调用方按原语义收盘）。
	/// </summary>
	private async Task DripAsync(Task peerCloseTask, CancellationToken token)
	{
		try
		{
			AppConfig config = configStore.Current;
			long nextDue = acceptedTicks + MillisecondsToTicks(config.Server.DelayMs);
			while (true)
			{
				if (!await WaitNextDripAsync(nextDue, peerCloseTask, token).ConfigureAwait(false))
				{
					return;
				}

				if (peerCloseTask.IsCompleted)
				{
					// 到点瞬间对端恰好离开（竞态兜底）：不再发这一行
					return;
				}

				AppConfig current = configStore.Current;
				byte[] line = RandomLine.Generate(current.Server.LineLength);
				await SendAllAsync(line, token).ConfigureAwait(false);
				Interlocked.Add(ref bytesSent, line.Length);
				nextDue += MillisecondsToTicks(current.Server.DelayMs);
			}
		}
		finally
		{
			// 停留时长的结束点 = 循环被打断的瞬间（设计 §2 步 6）：收尾还要等连接通知与地区查询，不能算进停留时长
			stayEndTicks = Stopwatch.GetTimestamp();
		}
	}

	/// <summary>
	/// 等待下一个慢滴时刻（设计 §2 步 6）：到点返回 true；等待期间对端先行离开（待读结束）或收到取消返回 false——
	/// 两者都立刻结束循环。按绝对值补余量，避免累积漂移。
	/// </summary>
	private static async Task<bool> WaitNextDripAsync(long dueTicks, Task peerCloseTask, CancellationToken token)
	{
		while (true)
		{
			long remainingTicks = dueTicks - Stopwatch.GetTimestamp();
			if (remainingTicks <= 0)
			{
				return true;
			}

			long remainingMs = Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency);
			Task delayTask = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remainingMs, int.MaxValue)), token);
			Task first = await Task.WhenAny(delayTask, peerCloseTask).ConfigureAwait(false);
			if (first == peerCloseTask)
			{
				return false;
			}

			try
			{
				await delayTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
		}
	}

	private async Task SendAllAsync(byte[] line, CancellationToken token)
	{
		int offset = 0;
		while (offset < line.Length)
		{
			int sent = await socket.SendAsync(line.AsMemory(offset), SocketFlags.None, token).ConfigureAwait(false);
			if (sent <= 0)
			{
				throw new IOException("socket send returned 0 bytes");
			}

			offset += sent;
		}
	}

	/// <summary>首行读取结束时取回身份串；超时 / 未上报 / 断言中断按空串处理（设计 §2.4）。</summary>
	private static async Task<string> CaptureBannerAsync(Task<string> bannerTask)
	{
		try
		{
			return await bannerTask.ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
			return "";
		}
	}

	/// <summary>SO_RCVBUF=1（best effort，设计 §2.3）：拖慢对端、减少本机缓冲占用；debug 打实际生效值。</summary>
	private void ApplyReceiveBufferLimit()
	{
		try
		{
			socket.ReceiveBufferSize = 1;
			Log.Debug($"SOCKET host={Host} port={RemotePort} rcvbuf={socket.ReceiveBufferSize}");
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ArgumentException)
		{
			Log.Debug($"SOCKET host={Host} port={RemotePort} rcvbuf=unavailable ({ex.GetType().Name}: {ex.Message})");
		}
	}

	/// <summary>收盘日志（设计 §6）：CLOSE host=... port=... time=秒.毫秒 bytes=发出字节；随后入队断开通知。</summary>
	private void LogClose()
	{
		// 停留时长的结束点 = 慢滴循环被打断的瞬间（DripAsync 退出时记点，设计 §2 步 6）；兜底当前时刻（防御）
		long endTicks = stayEndTicks != 0 ? stayEndTicks : Stopwatch.GetTimestamp();
		TimeSpan elapsed = Stopwatch.GetElapsedTime(acceptedTicks, endTicks);
		long sent = BytesSent;
		Stats.CountClose(elapsed.TotalSeconds, sent);
		Log.Info(string.Create(
			CultureInfo.InvariantCulture,
			$"CLOSE host={Host} port={RemotePort} time={elapsed.TotalSeconds:F3} bytes={sent}"));
		try
		{
			notify.EnqueueClose(CloseSnapshot(), elapsed.TotalSeconds, sent);
		}
		catch (Exception ex)
		{
			Log.Error($"NOTIFY enqueue_error kind=close host={Host} error={ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>
	/// 断开条所需的连接期快照（设计 §3.1）：正常路径由连接通知入队时产出；
	/// 连接通知入队异常（N4 兜底路径）时用本地已知信息兜底，保证断开条不因通知链路故障而丢失。
	/// </summary>
	private ConnectionSnapshot CloseSnapshot() => connectSnapshot
		?? new ConnectionSnapshot(Host, RemotePort, LocalPort, banner, DateTimeOffset.UtcNow, 0, GatePassed: false, geo);

	private static (string Host, int RemotePort, int LocalPort) DescribeEndpoints(Socket socket)
	{
		string host = "?";
		int remotePort = 0;
		int localPort = 0;
		if (socket.RemoteEndPoint is IPEndPoint remote)
		{
			IPAddress address = remote.Address;
			if (address.IsIPv4MappedToIPv6)
			{
				address = address.MapToIPv4();
			}

			host = address.ToString();
			remotePort = remote.Port;
		}

		if (socket.LocalEndPoint is IPEndPoint local)
		{
			localPort = local.Port;
		}

		return (host, remotePort, localPort);
	}

	private static long MillisecondsToTicks(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;
}
