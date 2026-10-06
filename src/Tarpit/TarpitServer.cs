using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tarpit;

/// <summary>
/// 监听与容量门控、连接登记（设计 §2）；热重载时按需重建监听（端口/地址族变更），存量连接不受影响。
/// </summary>
internal sealed class TarpitServer
{
	private const int listenBacklog = 1024;
	private const int acceptErrorDelayMs = 100;
	private const int shutdownDrainSeconds = 5;

	private readonly ConfigStore configStore;
	private readonly NotifyPipeline notify;
	private readonly CapacityGate gate;
	private readonly ConcurrentDictionary<long, Connection> connections = new();
	private readonly CancellationTokenSource stopCts = new();
	private Socket? listener;
	private CancellationTokenSource? acceptCts;
	private Task? acceptTask;
	private long nextConnectionId;
	private int boundPort;
	private BindFamilyMode boundFamily;

	public TarpitServer(ConfigStore configStore, NotifyPipeline notify)
	{
		this.configStore = configStore;
		this.notify = notify;
		gate = new CapacityGate(configStore.Current.MaxClients);
	}

	/// <summary>绑定并开始 accept；绑定失败抛异常（启动路径据此退出码 1）。</summary>
	public void Start()
	{
		listener = CreateListener(configStore.Current);
		StartAcceptLoop();
	}

	/// <summary>热重载（设计 §4.3）：容量门控按新值调整；端口/地址族变了才重建监听。</summary>
	public async Task ApplyReloadAsync(AppConfig next)
	{
		gate.Resize(next.MaxClients);
		if (listener is null || (boundPort == next.Port && boundFamily == next.FamilyMode))
		{
			return;
		}

		await RebuildListenerAsync(next).ConfigureAwait(false);
	}

	/// <summary>优雅退出（设计 §5）：停 accept → 关闭全部连接（各自记 CLOSE）→ 等待收盘。</summary>
	public async Task ShutdownAsync()
	{
		await stopCts.CancelAsync().ConfigureAwait(false);
		if (acceptCts is not null)
		{
			await acceptCts.CancelAsync().ConfigureAwait(false);
		}

		listener?.Dispose();
		await IgnoreFailureAsync(acceptTask).ConfigureAwait(false);

		// accept 循环已退出，登记表不再增长：快照一次即可
		Connection[] pending = connections.Values.ToArray();
		foreach (Connection connection in pending)
		{
			connection.RequestClose();
		}

		if (pending.Length == 0)
		{
			return;
		}

		Task[] tasks = Array.ConvertAll(pending, connection => connection.Completion);
		try
		{
			await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(shutdownDrainSeconds)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Log.Error($"STOP {connections.Count} connection(s) did not close in time: {ex.GetType().Name}");
		}
	}

	private Socket CreateListener(AppConfig config)
	{
		(IPAddress address, bool dualMode) = ResolveBindAddress(config.FamilyMode);
		Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			socket.DualMode = dualMode;
		}

		socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
		socket.Bind(new IPEndPoint(address, config.Port));
		socket.Listen(listenBacklog);
		boundPort = config.Port;
		boundFamily = config.FamilyMode;
		Log.Info($"LISTEN family={config.BindFamily} port={config.Port} address={address}");
		return socket;
	}

	/// <summary>auto = 双栈（IPv6Any + DualMode，对齐 endlessh 的 AF_UNSPEC）。</summary>
	private static (IPAddress Address, bool DualMode) ResolveBindAddress(BindFamilyMode mode) => mode switch
	{
		BindFamilyMode.Ipv4 => (IPAddress.Any, false),
		BindFamilyMode.Ipv6 => (IPAddress.IPv6Any, false),
		_ => Socket.OSSupportsIPv6 ? (IPAddress.IPv6Any, true) : (IPAddress.Any, false),
	};

	private void StartAcceptLoop()
	{
		Socket listenerSocket = listener ?? throw new InvalidOperationException("listener is not started");
		acceptCts = new CancellationTokenSource();
		CancellationToken token = acceptCts.Token;
		acceptTask = Task.Run(() => AcceptLoopAsync(listenerSocket, token));
	}

	private async Task RebuildListenerAsync(AppConfig next)
	{
		Log.Info($"LISTEN rebuild port={boundPort} -> {next.Port} family={next.BindFamily}");
		CancellationTokenSource? oldCts = acceptCts;
		Task? oldTask = acceptTask;
		if (oldCts is not null)
		{
			await oldCts.CancelAsync().ConfigureAwait(false);
		}

		listener?.Dispose();
		await IgnoreFailureAsync(oldTask).ConfigureAwait(false);
		oldCts?.Dispose();
		listener = CreateListener(next);
		StartAcceptLoop();
	}

	private async Task AcceptLoopAsync(Socket listenerSocket, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await gate.AcquireAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			Socket client;
			try
			{
				client = await listenerSocket.AcceptAsync(token).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
			{
				gate.Release();
				return;
			}
			catch (SocketException ex)
			{
				gate.Release();
				Log.Error($"ACCEPT failed: {ex.SocketErrorCode}");
				try
				{
					await Task.Delay(acceptErrorDelayMs, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}

				continue;
			}

			HandleAccepted(client);
		}
	}

	private void HandleAccepted(Socket client)
	{
		Connection connection = new(client, configStore, notify);
		long id = Interlocked.Increment(ref nextConnectionId);
		connections[id] = connection;
		Stats.CountConnect();
		Log.Info($"ACCEPT host={connection.Host} port={connection.RemotePort} "
			+ $"local={connection.LocalPort} n={connections.Count}/{gate.CurrentMax}");
		connection.Start(stopCts.Token);
		_ = connection.Completion.ContinueWith(
			_ => ReleaseConnection(id),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private void ReleaseConnection(long id)
	{
		connections.TryRemove(id, out _);
		gate.Release();
	}

	private static async Task IgnoreFailureAsync(Task? task)
	{
		if (task is null)
		{
			return;
		}

		try
		{
			await task.ConfigureAwait(false);
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
		}
	}
}
