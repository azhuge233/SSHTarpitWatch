using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Geo;
using SSHTarpitWatch.Notify;
using SSHTarpitWatch.Tarpit;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 地区解析端到端（设计 §3.6 / §7）：geoip 开启 + 合成夹具（自造）→ 真实监听 + 真实连接
/// → FakeNotifier 捕获的断开通知载荷含「地区：…」与「ISP：…」；未启用时整行不出现。
/// </summary>
[TestClass]
public sealed class GeoIntegrationTests
{
	[TestMethod]
	public async Task Server_GeoipEnabled_FixtureDatabase_NotificationCarriesRegionAndIsp()
	{
		using TempDirectory directory = new();
		byte[] database = GeoTestSupport.MmdbBytes("127.0.0.0/8", GeoTestSupport.FullRecord());
		directory.Write("city.mmdb", database);

		AppConfig config = TestSupport.Notifications(dedupSeconds: 0, perMinute: 600, onConnect: false);
		config.Server.Port = TestSupport.FreePort();
		config.Server.DelayMs = 1000;
		config.Notifications.Geoip = new GeoIpConfig { Enabled = true, Databases = ["city.mmdb"], Language = "zh" };

		using ConsoleCapture log = new();
		await using Harness harness = new(config, directory.Root, log);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-geo\n");
		client.Close();

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开", StringComparison.Ordinal)), "断开通知送达");
		string text = harness.Notifier.Texts.First(item => item.Contains("连接断开", StringComparison.Ordinal));

		StringAssert.Contains(text, "地区：中国 · 广东省 · 深圳市 (CN)");
		StringAssert.Contains(text, "ISP：Chinanet");
		Assert.AreEqual(1, harness.Geo.OpenCount, "夹具库应被打开");
	}

	[TestMethod]
	public async Task Server_GeoipDisabled_NoRegionLine()
	{
		AppConfig config = TestSupport.Notifications(dedupSeconds: 0, perMinute: 600, onConnect: false);
		config.Server.Port = TestSupport.FreePort();
		config.Server.DelayMs = 1000;

		using ConsoleCapture log = new();
		await using Harness harness = new(config, AppContext.BaseDirectory, log);
		using Socket client = Connect(harness.Port);
		SendAll(client, "SSH-2.0-plain\n");
		client.Close();

		await TestSupport.WaitUntilAsync(
			() => harness.Notifier.Texts.Any(text => text.Contains("连接断开", StringComparison.Ordinal)), "断开通知送达");
		string text = harness.Notifier.Texts.First(item => item.Contains("连接断开", StringComparison.Ordinal));

		Assert.IsFalse(text.Contains("地区：", StringComparison.Ordinal), "enabled=false → 整行不出现");
		Assert.IsFalse(text.Contains("ISP：", StringComparison.Ordinal));
	}

	/// <summary>进程内真实服务（真实端口 + FakeNotifier 渠道 + 地区查询门面）。</summary>
	private sealed class Harness : IAsyncDisposable
	{
		private readonly ConsoleCapture log;

		public Harness(AppConfig config, string geoipBaseDirectory, ConsoleCapture log)
		{
			this.log = log;
			Store = TestSupport.Store(config);
			Notifier = new FakeNotifier();
			Pipeline = new NotifyPipeline(Store, Notifier);
			Geo = new GeoLookup(Store, geoipBaseDirectory);
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
}
