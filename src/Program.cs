using System.Globalization;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;
using SSHTarpitWatch.Notify;
using SSHTarpitWatch.Tarpit;

namespace SSHTarpitWatch;

/// <summary>薄入口：CLI 解析、启动与接线（业务逻辑在各模块：Config / Tarpit / Notify / Infra）。</summary>
internal static class Program
{
	private const int pollIntervalMs = 100;

	private static async Task<int> Main(string[] args)
	{
		CliOptions options = CliOptions.Parse(args);
		if (options.Error is not null)
		{
			Console.Error.WriteLine($"sshtarpitwatch: {options.Error}");
			Console.Error.WriteLine(CliOptions.UsageHint);
			return 1;
		}

		if (options.ShowHelp)
		{
			Console.Out.WriteLine(CliOptions.HelpText);
			return 0;
		}

		if (options.ShowVersion)
		{
			Console.Out.WriteLine(AppVersion.Line);
			return 0;
		}

		if (options.TestNotify)
		{
			return await RunTestNotifyAsync(options.ConfigPath).ConfigureAwait(false);
		}

		return await RunServerAsync(options.ConfigPath).ConfigureAwait(false);
	}

	/// <summary>
	/// --test-notify（设计 §4.4）：加载并校验配置 → 绕过去重闸与全局限速闸（不写账本、不计入当日计数）
	/// → 用真实 INotifier 同步发一条测试通知 → 退出码 0=成功 / 1=配置错误 / 2=发送失败（错误信息不含 token，§9）。
	/// </summary>
	private static async Task<int> RunTestNotifyAsync(string configPath)
	{
		ConfigLoadResult load = ConfigLoader.Load(configPath);
		if (load.Errors.Count > 0)
		{
			ReportConfigErrors(load.Errors);
			return 1;
		}

		foreach (string warning in load.Warnings)
		{
			Console.Error.WriteLine($"sshtarpitwatch: warning: {warning}");
		}

		TelegramConfig telegram = load.Config.Notifications.Telegram;
		if (!telegram.Enabled || telegram.BotToken.Length == 0 || telegram.ChatId.Length == 0)
		{
			Console.Error.WriteLine("sshtarpitwatch: --test-notify requires notifications.telegram.enabled=true "
				+ "with non-empty bot_token and chat_id");
			return 1;
		}

		using TelegramNotifier notifier = new();
		string text = NotifyMessage.BuildTest(
			NotifyMessage.ResolveNickname(load.Config.Nickname), TimeProvider.System.GetLocalNow());
		NotifySendResult result = await notifier.SendAsync(text, load.Config.Notifications, CancellationToken.None)
			.ConfigureAwait(false);
		if (result.Success)
		{
			Console.Out.WriteLine("sshtarpitwatch: test notification sent");
			return 0;
		}

		Console.Error.WriteLine($"sshtarpitwatch: test notification failed: {result.Error}");
		return 2;
	}

	private static async Task<int> RunServerAsync(string configPath)
	{
		ConfigLoadResult load = ConfigLoader.Load(configPath);
		if (load.Errors.Count > 0)
		{
			ReportConfigErrors(load.Errors);
			return 1;
		}

		ConfigStore store = new(load.Config);
		Log.Level = store.Current.Level;
		foreach (string warning in load.Warnings)
		{
			Log.Info($"CONFIG warning=\"{warning}\"");
		}

		Log.Info($"CONFIG {Describe(store.Current)}");

		using SignalWatcher signals = new();
		using TelegramNotifier notifier = new();
		NotifyPipeline pipeline = new(store, notifier);
		TarpitServer server = new(store, pipeline);
		try
		{
			server.Start();
		}
		catch (Exception ex)
		{
			Log.Error($"START failed: {ex.GetType().Name}: {ex.Message}");
			return 1;
		}

		while (!signals.TerminateRequested)
		{
			if (SignalWatcher.ConsumeTotalsRequest())
			{
				Log.Info(Stats.FormatTotals());
			}

			if (signals.ReloadRequested)
			{
				signals.AcknowledgeReload();
				await ReloadAsync(configPath, store, server).ConfigureAwait(false);
			}

			await Task.Delay(pollIntervalMs).ConfigureAwait(false);
		}

		Log.Info("STOP signal received; closing connections");
		await server.ShutdownAsync().ConfigureAwait(false);
		await pipeline.ShutdownAsync(NotifyPipeline.DrainQueueTimeout).ConfigureAwait(false);
		Log.Info(Stats.FormatTotals());
		return 0;
	}

	/// <summary>SIGHUP 热重载（设计 §4.3）：失败保留旧配置、进程不退出；成功打印新配置。</summary>
	private static async Task ReloadAsync(string configPath, ConfigStore store, TarpitServer server)
	{
		if (!File.Exists(configPath))
		{
			// 文件缺失按重载失败处理：静默回落到默认值会关掉通知、还可能改端口
			Log.Error($"reload rejected: config file not found: {configPath} (keeping previous configuration)");
			return;
		}

		ConfigLoadResult load = ConfigLoader.Load(configPath);
		if (load.Errors.Count > 0)
		{
			Log.Error($"reload rejected: {string.Join("; ", load.Errors)} (keeping previous configuration)");
			return;
		}

		store.Replace(load.Config);
		Log.Level = load.Config.Level;
		await server.ApplyReloadAsync(load.Config).ConfigureAwait(false);
		Log.Info($"RELOAD ok {Describe(load.Config)}");
	}

	private static void ReportConfigErrors(IReadOnlyList<string> errors)
	{
		foreach (string error in errors)
		{
			Console.Error.WriteLine($"sshtarpitwatch: config error: {error}");
		}
	}

	/// <summary>配置摘要（不含 bot_token，设计 §9）。</summary>
	private static string Describe(AppConfig config) => string.Create(
		CultureInfo.InvariantCulture,
		$"nickname=\"{config.Nickname}\" port={config.Port} bind_family={config.BindFamily} delay_ms={config.DelayMs} "
		+ $"line_length={config.LineLength} max_clients={config.MaxClients} log_level={config.LogLevelName} "
		+ $"telegram_enabled={config.Notifications.Telegram.Enabled} notify_on_connect={config.Notifications.NotifyOnConnect} "
		+ $"notify_on_close={config.Notifications.NotifyOnClose} dedup_per_ip_seconds={config.Notifications.DedupPerIpSeconds} "
		+ $"max_per_minute={config.Notifications.MaxPerMinute}");
}
