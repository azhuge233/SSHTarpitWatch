using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Geo;
using SSHTarpitWatch.Notify;
using SSHTarpitWatch.Tarpit;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 真实服务集成（设计 §7）：进程内起 <see cref="TarpitServer"/>（真实监听 + 真实连接处理 + 真实通知管道，
/// 渠道注入 <see cref="FakeNotifier"/>），TCP 客户端逐项断言——首行时序锚定与行间隔、行内容合法性、
/// 连接通知载荷、CLOSE 日志与计数、容量 backlog。时间断言一律宽裕容差 + 轮询等待（Windows/WSL 双端稳定优先）。
/// </summary>
[TestClass]
public sealed class TarpitServerTests
{
	/// <summary>快速慢滴步长（真实时间；断言容差覆盖调度抖动）。</summary>
	private const int fastDelayMs = 100;

	[TestMethod]
	public async Task Server_FirstLineAndIntervals_AnchoredToAccept()
	{
		const int delayMs = 200;
		await using Harness harness = StartServer(delayMs: delayMs);
		using Socket client = Connect(harness.Port);

		// 连上不发数据：首行应于 accept + delay_ms 到达（计时锚定 accept，而非首包）
		long start = Stopwatch.GetTimestamp();
		Frame first = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "首行");
		double firstMs = Stopwatch.GetElapsedTime(start, first.Ticks).TotalMilliseconds;
		Assert.IsTrue(firstMs >= delayMs * 0.5, $"首行过早：{firstMs:F0}ms（delay={delayMs}ms）");
		Assert.IsTrue(firstMs <= delayMs + 500, $"首行过迟：{firstMs:F0}ms（delay={delayMs}ms）");

		// 后续每 delay_ms 一行
		Frame second = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "第二行");
		Frame third = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "第三行");
		AssertInterval(first, second, delayMs, "第一→第二行");
		AssertInterval(second, third, delayMs, "第二→第三行");
	}

	[TestMethod]
	public async Task Server_Lines_PrintableCrLfBoundedNeverSsh()
	{
		const int lineLength = 40;
		await using Harness harness = StartServer(delayMs: fastDelayMs, lineLength: lineLength);
		using Socket client = Connect(harness.Port);

		for (int i = 1; i <= 4; i++)
		{
			Frame frame = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), $"第 {i} 行");
			byte[] bytes = frame.Bytes;
			Assert.IsTrue(
				bytes.Length >= 5 && bytes.Length <= lineLength + 2,
				$"行总长越界：{bytes.Length}（lineLength={lineLength}，第 {i} 行）");
			Assert.AreEqual((byte)'\r', bytes[^2], $"第 {i} 行应以 CRLF 结尾");
			Assert.AreEqual((byte)'\n', bytes[^1], $"第 {i} 行应以 CRLF 结尾");

			byte[] content = bytes[..^2];
			foreach (byte value in content)
			{
				Assert.IsTrue(value is >= 32 and <= 126, $"第 {i} 行含非可打印字节 {value}");
			}

			bool sshPrefix = content.Length >= 4
				&& content[0] == (byte)'S' && content[1] == (byte)'S'
				&& content[2] == (byte)'H' && content[3] == (byte)'-';
			Assert.IsFalse(sshPrefix, $"第 {i} 行以 SSH- 开头（设计 §2.1 禁止）");
		}
	}

	[TestMethod]
	public async Task Server_ConnectNotification_CarriesClientBanner()
	{
		// 连接通知在首行读取结束/超时后触发；delay 拉长避免慢滴干扰
		await using Harness harness = StartServer(delayMs: 1000);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-test\n");

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Count >= 1, "连接通知送达（Connection→管道→工作器）");
		string text = harness.Notifier.Texts[0];
		StringAssert.Contains(text, "新连接");
		StringAssert.Contains(text, "SSH-2.0-test");
	}

	[TestMethod]
	public async Task Server_ClientDisconnect_LogsCloseAndNotifiesClose()
	{
		await using Harness harness = StartServer(delayMs: fastDelayMs);
		using Socket client = Connect(harness.Port);
		Frame first = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "首行");

		client.Close();

		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "CLOSE host=") >= 1, "CLOSE 日志出现");
		Match close = Regex.Match(harness.LogText, @"CLOSE host=(\S+) port=(\d+) time=(\d+\.\d{3}) bytes=(\d+)");
		Assert.IsTrue(close.Success, "CLOSE 行应含 host/port/time/bytes");
		Assert.AreEqual("127.0.0.1", close.Groups[1].Value);
		long bytes = long.Parse(close.Groups[4].Value, CultureInfo.InvariantCulture);
		Assert.IsTrue(bytes >= first.Bytes.Length, $"bytes={bytes} 应至少涵盖已收到的首行（{first.Bytes.Length}）");

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开")), "断开通知送达");
		string closeText = harness.Notifier.Texts.First(text => text.Contains("连接断开"));
		StringAssert.Contains(closeText, "停留：");
		StringAssert.Contains(closeText, "发出：");
	}

	[TestMethod]
	public async Task Server_ClientDisconnectsEarly_DurationIsImmediate()
	{
		// 时长精度（设计 §2 步 6）：delay_ms=10000 时连接约 0.2 秒即断开——旧行为要等下一次慢滴写入才发现（记 ≥10 秒），
		// 新行为以待读感知到对端离开的瞬间为断开时刻；CLOSE 日志与断开通知两处断言
		await using Harness harness = StartServer(delayMs: 10000);
		using Socket client = Connect(harness.Port);

		await Task.Delay(200);
		client.Close();

		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "CLOSE host=") >= 1, "CLOSE 日志出现");
		Match close = Regex.Match(harness.LogText, @"CLOSE host=(\S+) port=(\d+) time=(\d+\.\d{3}) bytes=(\d+)");
		Assert.IsTrue(close.Success, "CLOSE 行应含 host/port/time/bytes");
		double logSeconds = double.Parse(close.Groups[3].Value, CultureInfo.InvariantCulture);
		Assert.IsTrue(logSeconds < 2.0, $"停留时长应即时感知对端关闭（<2s；旧行为 ≥10s）：CLOSE 实测 {logSeconds:F3}s");

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开")), "断开通知送达");
		string closeText = harness.Notifier.Texts.First(text => text.Contains("连接断开"));
		Match duration = Regex.Match(closeText, @"停留：(\d+) 小时 (\d+) 分 (\d+) 秒");
		Assert.IsTrue(duration.Success, "断开通知应含「停留：X 小时 Y 分 Z 秒」");
		double notifySeconds = (int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
			+ (int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
			+ int.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
		Assert.IsTrue(notifySeconds < 2, $"断开通知的停留时长应 <2s：实测 {notifySeconds}s（{duration.Value}）");
	}

	[TestMethod]
	public async Task Server_PeerSendsContinuously_ConnectionSurvivesAndDrips()
	{
		// 待读把对端数据一律丢弃并继续待读（设计 §2 步 6）：持续发数据不应让连接提前结束、慢滴应照常
		const int delayMs = 200; // 与 AssertInterval 的容差配套：下界 delayMs-150 仍具约束力
		const int sendIntervalMs = 100;
		await using Harness harness = StartServer(delayMs: delayMs);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-live\n");
		Frame first = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "首行");

		// 约 1.2 秒内每 100ms 发 1 字节：首行阶段已结束 → 这些字节都落在待读上
		using CancellationTokenSource sending = new(TimeSpan.FromSeconds(1.2));
		byte[] one = [(byte)'x'];
		Task sender = Task.Run(async () =>
		{
			try
			{
				while (true)
				{
					client.Send(one, SocketFlags.None);
					await Task.Delay(sendIntervalMs, sending.Token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// 发送窗口结束
			}
		});

		Frame second = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "对端发数据期间第二行");
		Frame third = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "对端发数据期间第三行");
		AssertInterval(first, second, delayMs, "第一→第二行（对端持续发数据）");
		AssertInterval(second, third, delayMs, "第二→第三行（对端持续发数据）");
		await sender;
		Assert.AreEqual(0, CountOccurrences(harness.LogText, "CLOSE host="), "对端持续发数据不应让连接提前结束（待读不误判）");

		// 数据流停下后真正断开：待读照常感知
		client.Close();
		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "CLOSE host=") >= 1, "断开后 CLOSE 日志出现");
	}

	[TestMethod]
	public async Task Server_Shutdown_ClosesAllConnectionsWithOneCloseLogEach()
	{
		await using Harness harness = StartServer(delayMs: 1000);
		using Socket first = Connect(harness.Port);
		using Socket second = Connect(harness.Port);
		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "ACCEPT host=") >= 2, "两条连接均被接受");

		await harness.Server.ShutdownAsync();

		Assert.AreEqual(2, CountOccurrences(harness.LogText, "CLOSE host="), "CLOSE 条数应等于连接数");
		Assert.IsFalse(harness.LogText.Contains("did not close in time"), "优雅退出不应出现收盘超时");
	}

	[TestMethod]
	public async Task Server_ShutdownWithPendingPeerRead_ClosesPromptly()
	{
		// 验收③（设计 §2 步 6）：待读挂在 ReceiveAsync 上时关停——连接照常记 CLOSE，等待不被吊住
		await using Harness harness = StartServer(delayMs: fastDelayMs);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-watch\n"); // 首行结束后待读进入挂起接收
		await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "首行");

		await harness.Server.ShutdownAsync();

		Assert.AreEqual(1, CountOccurrences(harness.LogText, "CLOSE host="), "关停应记一条 CLOSE");
		Assert.IsFalse(harness.LogText.Contains("did not close in time"), "待读不应吊住关停");
	}

	[TestMethod]
	public async Task Server_CapacityOne_SecondClientHeldInBacklogUntilSlotFrees()
	{
		await using Harness harness = StartServer(delayMs: fastDelayMs, maxClients: 1);
		using Socket slotHolder = Connect(harness.Port);
		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "ACCEPT host=") == 1, "A 被接受并占满容量");

		// B 的 TCP 握手由内核完成、滞留 listen backlog；容量满时服务端不 accept
		using Socket waiting = Connect(harness.Port);
		Frame? unexpected = await TryReadFrameAsync(waiting, TimeSpan.FromMilliseconds(fastDelayMs * 3));
		Assert.IsNull(unexpected, "B 未被 accept 前不应收到任何数据");
		Assert.AreEqual(1, CountOccurrences(harness.LogText, "ACCEPT host="), "容量满时不应出现第二条 ACCEPT");

		// A 断开释放空位 → B 被接受 → 开始慢滴
		slotHolder.Close();
		await TestSupport.WaitUntilAsync(
			() => CountOccurrences(harness.LogText, "ACCEPT host=") == 2, "空位释放后 B 被接受");
		Frame firstOfB = await ReadFrameAsync(waiting, TimeSpan.FromSeconds(3), "B 被接受后的首行");
		Assert.IsTrue(firstOfB.Bytes.Length >= 5, "B 应收到合法的慢滴行");
	}

	[TestMethod]
	public async Task Server_DefaultOnConnectOff_SendsOnlyOneMergedCloseNotification()
	{
		// 默认配置（filter.on_connect=false）：连接期不发通知，断开时收到唯一一条、含连接期全部信息（R3.2 / §3.3）
		await using Harness harness = StartServer(delayMs: 1000, onConnect: false);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-default\n");
		Frame first = await ReadFrameAsync(client, TimeSpan.FromSeconds(3), "首行");

		client.Close();

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开")), "断开通知送达");
		await Task.Delay(50); // 给“误发的连接条”留出走漏窗口（若实现错误，连接条会先于此到达）

		Assert.AreEqual(1, harness.Notifier.CallCount, "默认配置下一次连接只发一条通知");
		string text = harness.Notifier.Texts[0];
		StringAssert.Contains(text, "来源：127.0.0.1:");
		StringAssert.Contains(text, $"→ 本机 :{harness.Port}");
		StringAssert.Contains(text, "客户端：SSH-2.0-default");
		StringAssert.Contains(text, "时间：");
		StringAssert.Contains(text, "停留：");
		StringAssert.Contains(text, "发出：");
		StringAssert.Contains(text, "该 IP 今日第 1 次");
		Assert.IsFalse(text.Contains("新连接", StringComparison.Ordinal), "默认不发连接条");
		Assert.IsTrue(first.Bytes.Length >= 5, "慢滴行为不受影响");
	}

	[TestMethod]
	public async Task Server_OnConnectTrue_SendsConnectThenMergedClose()
	{
		// on_connect=true：连接条模板不变（无停留/字节），断开条仍为合并模板（R3.3）
		await using Harness harness = StartServer(delayMs: 1000, onConnect: true);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-two\n");

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("新连接")), "连接条送达");
		string connectText = harness.Notifier.Texts.First(text => text.Contains("新连接"));
		StringAssert.Contains(connectText, "客户端：SSH-2.0-two");
		Assert.IsFalse(connectText.Contains("停留：", StringComparison.Ordinal), "连接条模板不变（无停留/字节）");

		client.Close();

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开")), "断开条送达");
		string closeText = harness.Notifier.Texts.First(text => text.Contains("连接断开"));
		StringAssert.Contains(closeText, "客户端：SSH-2.0-two");
		StringAssert.Contains(closeText, "停留：");
		StringAssert.Contains(closeText, "发出：");
		await TestSupport.WaitUntilAsync(() => harness.Notifier.CallCount >= 2, "连接条 + 断开条共两条");
	}

	// ---------- 测试底座 ----------

	/// <summary>一帧（含尾部 CRLF）与读毕时刻（单调时钟）。</summary>
	private readonly record struct Frame(byte[] Bytes, long Ticks);

	/// <summary>进程内真实服务（真实端口 + FakeNotifier 渠道 + 地区查询门面），连同 stdout 捕获一起收尾。</summary>
	private sealed class Harness : IAsyncDisposable
	{
		private readonly ConsoleCapture log;

		public Harness(AppConfig config, ConsoleCapture log)
		{
			this.log = log;
			Store = TestSupport.Store(config);
			Notifier = new FakeNotifier();
			Pipeline = new NotifyPipeline(Store, Notifier);
			Geo = new GeoLookup(Store, AppContext.BaseDirectory);
			Geo.Open();
			Server = new TarpitServer(Store, Pipeline, Geo);
			Server.Start();
		}

		public ConfigStore Store { get; }

		public FakeNotifier Notifier { get; }

		public NotifyPipeline Pipeline { get; }

		public GeoLookup Geo { get; }

		public TarpitServer Server { get; }

		public int Port => Store.Current.Server.Port;

		public string LogText => log.Text;

		public async ValueTask DisposeAsync()
		{
			try
			{
				await Server.ShutdownAsync();
				await Pipeline.DisposeAsync();
			}
			finally
			{
				Geo.Dispose();
				log.Dispose();
			}
		}
	}

	private static Harness StartServer(
		int delayMs = fastDelayMs, int lineLength = 32, int maxClients = 8, bool onConnect = true)
	{
		AppConfig config = TestSupport.Notifications(dedupSeconds: 0, perMinute: 600, onConnect: onConnect);
		config.Server.Port = TestSupport.FreePort();
		config.Server.DelayMs = delayMs;
		config.Server.LineLength = lineLength;
		config.Server.MaxClients = maxClients;

		ConsoleCapture log = new();
		try
		{
			return new Harness(config, log);
		}
		catch
		{
			log.Dispose();
			throw;
		}
	}

	private static Socket Connect(int port)
	{
		Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			socket.Connect(IPAddress.Loopback, port);
			return socket;
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private static void SendAll(Socket socket, string text)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(text);
		int offset = 0;
		while (offset < bytes.Length)
		{
			offset += socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
		}
	}

	/// <summary>读一帧（含尾 CRLF）；窗口内未收到即断言失败（用途写进断言消息）。</summary>
	private static async Task<Frame> ReadFrameAsync(Socket socket, TimeSpan timeout, string what)
	{
		Frame? frame = await TryReadFrameAsync(socket, timeout);
		Assert.IsNotNull(frame, $"{what}：{timeout.TotalMilliseconds:F0}ms 内未收到完整行");
		return frame.Value;
	}

	/// <summary>尝试读一帧（含尾 CRLF）；窗口内未收齐或对端关闭 → null（用于"窗口内无数据"断言）。</summary>
	private static async Task<Frame?> TryReadFrameAsync(Socket socket, TimeSpan timeout)
	{
		byte[] buffer = new byte[1024];
		int total = 0;
		using CancellationTokenSource cts = new(timeout);
		try
		{
			while (total < buffer.Length)
			{
				int received = await socket.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None, cts.Token);
				if (received <= 0)
				{
					return null;
				}

				total += received;
				for (int i = total - received; i < total; i++)
				{
					if (i > 0 && buffer[i] == (byte)'\n' && buffer[i - 1] == (byte)'\r')
					{
						return new Frame(buffer[..(i + 1)], Stopwatch.GetTimestamp());
					}
				}
			}

			return null; // 1024 字节仍不见 CRLF：按未收齐处理（line_length 上限 255，实际不会发生）
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (SocketException)
		{
			return null;
		}
	}

	private static void AssertInterval(Frame from, Frame to, int delayMs, string what)
	{
		double interval = Stopwatch.GetElapsedTime(from.Ticks, to.Ticks).TotalMilliseconds;
		Assert.IsTrue(interval >= delayMs - 150, $"{what}间隔过短：{interval:F0}ms（delay={delayMs}ms）");
		Assert.IsTrue(interval <= delayMs + 500, $"{what}间隔过长：{interval:F0}ms（delay={delayMs}ms）");
	}

	private static int CountOccurrences(string text, string needle)
	{
		int count = 0;
		int index = text.IndexOf(needle, StringComparison.Ordinal);
		while (index >= 0)
		{
			count++;
			index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
		}

		return count;
	}
}
