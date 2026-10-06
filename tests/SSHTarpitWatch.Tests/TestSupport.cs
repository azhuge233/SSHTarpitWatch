using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Tests;

/// <summary>测试共用的配置构造与等待工具。</summary>
internal static class TestSupport
{
	/// <summary>统一起始时刻（UTC）。</summary>
	public static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

	/// <summary>探测一个空闲 TCP 端口（绑定后立即释放；供起真实服务的测试使用）。</summary>
	public static int FreePort()
	{
		TcpListener probe = new(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	/// <summary>构造通知配置（默认：渠道开、两开关开、去重关、限速 60/分）。</summary>
	public static AppConfig Notifications(
		bool enabled = true,
		bool onConnect = true,
		bool onClose = true,
		int dedupSeconds = 0,
		int perMinute = 60,
		string nickname = "test-host",
		string token = "TEST-TOKEN",
		string chatId = "42")
	{
		AppConfig config = new() { Nickname = nickname };
		config.Notifications = new NotificationConfig
		{
			NotifyOnConnect = onConnect,
			NotifyOnClose = onClose,
			DedupPerIpSeconds = dedupSeconds,
			MaxPerMinute = perMinute,
			Telegram = new TelegramConfig { Enabled = enabled, BotToken = token, ChatId = chatId },
		};
		return config;
	}

	public static ConfigStore Store(AppConfig config) => new(config);

	/// <summary>轮询等待条件成立（真实时间上限 timeoutMs）；超时即以断言失败。</summary>
	public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
	{
		long deadline = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > deadline)
			{
				Assert.Fail($"timed out waiting for: {what}");
			}

			await Task.Delay(10).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// 推进假时钟后断言“仍无新调用”：用于退避序列的“未到点不重试”检查
	/// （时钟只在本调用里前进，工作器只可能被定时器唤醒）。
	/// </summary>
	public static async Task AdvanceExpectingNoCallAsync(
		FakeTimeProvider fake, Func<int> callCount, int expectedCalls, TimeSpan delta)
	{
		fake.Advance(delta);
		await Task.Delay(100).ConfigureAwait(false);
		Assert.AreEqual(expectedCalls, callCount(), $"推进 {delta.TotalSeconds}s 后不应有新调用");
	}
}

/// <summary>捕获进程 stdout（Log 输出）用于断言，测试结束时还原；线程安全。</summary>
internal sealed class ConsoleCapture : IDisposable
{
	private readonly TextWriter original;
	private readonly CapturingWriter writer = new();

	public ConsoleCapture()
	{
		original = Console.Out;
		Console.SetOut(writer);
	}

	public string Text => writer.ToString() ?? "";

	public void Dispose() => Console.SetOut(original);

	private sealed class CapturingWriter : TextWriter
	{
		private readonly object sync = new();
		private readonly System.Text.StringBuilder builder = new();

		public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

		public override void Write(char value)
		{
			lock (sync)
			{
				builder.Append(value);
			}
		}

		public override void Write(string? value)
		{
			lock (sync)
			{
				builder.Append(value);
			}
		}

		public override void WriteLine(string? value)
		{
			lock (sync)
			{
				builder.Append(value).Append('\n');
			}
		}

		public override string ToString()
		{
			lock (sync)
			{
				return builder.ToString();
			}
		}
	}
}
