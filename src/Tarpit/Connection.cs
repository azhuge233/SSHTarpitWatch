using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tarpit;

/// <summary>
/// 单条连接（设计 §2）：读客户端首行（与慢滴并发、不阻塞发送）+ 按 accept 锚点慢滴随机行；
/// 首行读取结束/超时后触发连接通知（步 5），收盘记 CLOSE 并入队断开通知；SO_RCVBUF=1 为 best effort。
/// </summary>
internal sealed class Connection
{
	private readonly Socket socket;
	private readonly ConfigStore configStore;
	private readonly NotifyPipeline notify;
	private readonly CancellationTokenSource closeCts = new();
	private readonly long acceptedTicks;
	private long bytesSent;
	private string banner = "";
	private Task connectNotifyTask = Task.CompletedTask;
	private bool connectGatePassed;

	public Connection(Socket socket, ConfigStore configStore, NotifyPipeline notify)
	{
		this.socket = socket;
		this.configStore = configStore;
		this.notify = notify;
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
		try
		{
			await DripAsync(linked.Token).ConfigureAwait(false);
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
			await linked.CancelAsync().ConfigureAwait(false);
			await AwaitConnectNotifyAsync().ConfigureAwait(false);
			LogClose();
			socket.Dispose();
		}
	}

	/// <summary>设计 §2 步 5：首行读取结束/超时后触发连接通知；取回身份串并记住去重闸结果供断开条跟随。</summary>
	private async Task FireConnectNotifyAsync(Task<string> bannerTask)
	{
		banner = await CaptureBannerAsync(bannerTask).ConfigureAwait(false);
		try
		{
			connectGatePassed = notify.EnqueueConnect(Host, RemotePort, LocalPort, banner).GatePassed;
		}
		catch (Exception ex)
		{
			// N4：通知链路故障绝不影响困住逻辑
			Log.Error($"NOTIFY enqueue_error kind=connect host={Host} error={ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>确保 connect 事件先于 close 事件入队（首行读取可能在收盘时才被取消收尾）。</summary>
	private async Task AwaitConnectNotifyAsync()
	{
		try
		{
			await connectNotifyTask.ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
		}
	}

	/// <summary>
	/// 慢滴循环：首行于 accept + delay_ms 发出，此后每 delay_ms 一行（单调时钟锚定 accept 时刻）。
	/// 每轮取当前配置快照，热重载即刻生效；写失败即收盘（由调用方处理）。
	/// </summary>
	private async Task DripAsync(CancellationToken token)
	{
		AppConfig config = configStore.Current;
		long nextDue = acceptedTicks + MillisecondsToTicks(config.DelayMs);
		while (true)
		{
			if (!await DelayUntilAsync(nextDue, token).ConfigureAwait(false))
			{
				return;
			}

			AppConfig current = configStore.Current;
			byte[] line = RandomLine.Generate(current.LineLength);
			await SendAllAsync(line, token).ConfigureAwait(false);
			Interlocked.Add(ref bytesSent, line.Length);
			nextDue += MillisecondsToTicks(current.DelayMs);
		}
	}

	/// <summary>读到 dueTicks（单调时钟）为止；被取消返回 false。按绝对值补余量，避免累积漂移。</summary>
	private static async Task<bool> DelayUntilAsync(long dueTicks, CancellationToken token)
	{
		while (true)
		{
			long remainingTicks = dueTicks - Stopwatch.GetTimestamp();
			if (remainingTicks <= 0)
			{
				return true;
			}

			long remainingMs = Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency);
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remainingMs, int.MaxValue)), token).ConfigureAwait(false);
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
		TimeSpan elapsed = Stopwatch.GetElapsedTime(acceptedTicks);
		long sent = BytesSent;
		Stats.CountClose(elapsed.TotalSeconds, sent);
		Log.Info(string.Create(
			CultureInfo.InvariantCulture,
			$"CLOSE host={Host} port={RemotePort} time={elapsed.TotalSeconds:F3} bytes={sent}"));
		try
		{
			notify.EnqueueClose(Host, RemotePort, elapsed.TotalSeconds, sent, connectGatePassed);
		}
		catch (Exception ex)
		{
			Log.Error($"NOTIFY enqueue_error kind=close host={Host} error={ex.GetType().Name}: {ex.Message}");
		}
	}

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
