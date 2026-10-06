namespace SSHTarpitWatch.Infra;

/// <summary>命令行解析结果（设计 §4.4）；Error 非空表示用法错误——用法到 stderr + 退出码 1。</summary>
internal sealed record CliOptions(string ConfigPath, bool ShowHelp, bool ShowVersion, bool TestNotify, string? Error)
{
	/// <summary>默认配置路径 = 二进制所在目录的 config.json（与启动时的工作目录无关；systemd 下无需 WorkingDirectory）。</summary>
	public static readonly string DefaultConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

	public const string UsageHint =
		"Usage: sshtarpitwatch [--config <path>] [--version] [--help] [--test-notify]";

	public const string HelpText =
		"""
		SSHTarpitWatch — SSH tarpit with connection notifications

		Usage: sshtarpitwatch [options]

		  --config <path>   Configuration file (default: config.json next to the binary)
		  --version         Print version and exit
		  --help            Print this help and exit
		  --test-notify     Load and validate config, send one test notification, then exit
		                    (bypasses dedup and rate limits; exits 1 on config error, 2 on send failure)
		  (no options)      Start the tarpit server
		                    signals: SIGHUP = reload config, SIGUSR1 = print totals,
		                             SIGTERM / SIGINT = graceful shutdown

		Exit codes: 0 = success, 1 = usage or configuration error, 2 = test notification failed
		""";

	/// <summary>解析 argv；未知/多余参数记入 Error（调用方负责打印用法并退出 1）。</summary>
	public static CliOptions Parse(string[] args)
	{
		string configPath = DefaultConfigPath;
		bool showHelp = false;
		bool showVersion = false;
		bool testNotify = false;
		string? error = null;

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			if (arg.StartsWith("--config=", StringComparison.Ordinal))
			{
				configPath = arg["--config=".Length..];
				continue;
			}

			switch (arg)
			{
				case "--config":
					if (i + 1 >= args.Length)
					{
						error ??= "option --config requires a path argument";
						break;
					}

					configPath = args[++i];
					break;
				case "--version":
					showVersion = true;
					break;
				case "--help":
					showHelp = true;
					break;
				case "--test-notify":
					testNotify = true;
					break;
				default:
					error ??= $"unknown option: {arg}";
					break;
			}
		}

		return new CliOptions(configPath, showHelp, showVersion, testNotify, error);
	}
}
