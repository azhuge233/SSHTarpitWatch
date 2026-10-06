using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 配置加载与校验（设计 §4.1 / §4.2 / §7）：全部默认值、宽松 JSON（注释与尾逗号）、
/// 文件缺失 / 非法 JSON / 空文档行为、校验矩阵（非法值各报对应错 + 组合多条）。
/// 临时配置文件一律落 <see cref="Path.GetTempPath"/>，[TestCleanup] 清理不遗留。
/// </summary>
[TestClass]
public sealed class ConfigLoaderTests
{
	private readonly List<string> tempFiles = [];
	private readonly List<string> tempDirs = [];

	[TestCleanup]
	public void CleanupTempFiles()
	{
		foreach (string path in tempFiles)
		{
			File.Delete(path);
		}

		tempFiles.Clear();
		foreach (string dir in tempDirs)
		{
			Directory.Delete(dir);
		}

		tempDirs.Clear();
	}

	[TestMethod]
	public void Load_MissingFileOrEmptyObject_UsesDefaults()
	{
		// 文件缺失：可继续运行（Warnings 非空）+ 全默认值 + 通知关闭
		string missing = Path.Combine(Path.GetTempPath(), $"sshtw-missing-{Guid.NewGuid():N}.json");
		Assert.IsFalse(File.Exists(missing), "测试前置：该路径不应存在");

		ConfigLoadResult missingResult = ConfigLoader.Load(missing);
		Assert.AreEqual(0, missingResult.Errors.Count, "缺失文件不应报错（警告 + 默认值继续运行）");
		Assert.AreEqual(1, missingResult.Warnings.Count, "缺失文件应恰好一条警告");
		StringAssert.Contains(missingResult.Warnings[0], "not found");
		AssertDefaults(missingResult.Config, "缺失文件");
		Assert.IsFalse(missingResult.Config.Notifications.Telegram.Enabled, "缺失文件时通知应关闭");

		// 空对象：全默认值、无错误无警告
		ConfigLoadResult emptyResult = ConfigLoader.Load(WriteTemp("{}"));
		Assert.AreEqual(0, emptyResult.Errors.Count);
		Assert.AreEqual(0, emptyResult.Warnings.Count);
		AssertDefaults(emptyResult.Config, "空对象");

		// 显式 null 字段：Normalize 兜底后同样落回默认值
		ConfigLoadResult nullResult = ConfigLoader.Load(WriteTemp(
			"""{"nickname": null, "bind_family": null, "log_level": null, "notifications": null}"""));
		Assert.AreEqual(0, nullResult.Errors.Count, "显式 null 字段应经 Normalize 兜底");
		AssertDefaults(nullResult.Config, "显式 null 字段");
		Assert.IsNotNull(nullResult.Config.Notifications.Telegram, "null 的通知段应被重建");
	}

	[TestMethod]
	public void Load_LenientJson_CommentsAndTrailingCommasAccepted()
	{
		string json = """
			{
			  // 行注释：监听端口
			  "port": 8080,
			  "delay_ms": 250,
			  "line_length": 64, /* 块注释：行长上限 */
			  "bind_family": "ipv4",
			  "max_clients": 7,
			  "log_level": "debug",
			  "notifications": {
			    "telegram": { "enabled": true, "bot_token": "T", "chat_id": "C", },
			    "dedup_per_ip_seconds": 0,
			    "max_per_minute": 9,
			  },
			}
			""";

		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(json));

		Assert.AreEqual(0, result.Errors.Count);
		Assert.AreEqual(0, result.Warnings.Count);
		Assert.AreEqual(8080, result.Config.Port);
		Assert.AreEqual(250, result.Config.DelayMs);
		Assert.AreEqual(64, result.Config.LineLength);
		Assert.AreEqual("ipv4", result.Config.BindFamily);
		Assert.AreEqual(7, result.Config.MaxClients);
		Assert.AreEqual("debug", result.Config.LogLevelName);
		Assert.IsTrue(result.Config.Notifications.Telegram.Enabled);
		Assert.AreEqual("T", result.Config.Notifications.Telegram.BotToken);
		Assert.AreEqual("C", result.Config.Notifications.Telegram.ChatId);
		Assert.AreEqual(0, result.Config.Notifications.DedupPerIpSeconds);
		Assert.AreEqual(9, result.Config.Notifications.MaxPerMinute);
	}

	[TestMethod]
	public void Load_InvalidJsonAndNullDocument_ReportErrors()
	{
		ConfigLoadResult broken = ConfigLoader.Load(WriteTemp("{ \"port\": "));
		Assert.IsTrue(broken.Errors.Count > 0, "非法 JSON 应报错");
		AssertDefaults(broken.Config, "非法 JSON 回退默认值");

		ConfigLoadResult nullDocument = ConfigLoader.Load(WriteTemp("null"));
		Assert.IsTrue(nullDocument.Errors.Count > 0, "空文档（null）应报错");
		StringAssert.Contains(nullDocument.Errors[0], "empty");
		AssertDefaults(nullDocument.Config, "空文档回退默认值");
	}

	[TestMethod]
	public void Load_PathIsDirectory_ReportsUnreadableErrorWithoutThrowing()
	{
		// 回归保护：读取失败（目录 / 权限不足）必须明确报错且不抛异常——
		// 静默回落默认值会关掉通知，属于危险行为（启动路径退出码 1，重载路径保留旧配置）
		string directory = Path.Combine(Path.GetTempPath(), $"sshtw-dir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		tempDirs.Add(directory);

		ConfigLoadResult result = ConfigLoader.Load(directory);

		Assert.IsTrue(result.Errors.Count > 0, "目录不可读应报错（不得静默回落默认值）");
		StringAssert.Contains(result.Errors[0], "cannot read config file");
		Assert.AreEqual(0, result.Warnings.Count, "不可读不是『缺失』，不应报缺失警告");
		AssertDefaults(result.Config, "不可读路径回退默认值");
	}

	[TestMethod]
	public void Validate_Matrix_EachIllegalValueAndCombination()
	{
		Assert.AreEqual(0, ValidConfig().Validate().Count, "合法配置不应有任何错误");

		// 每项非法值各报对应一条错误
		(string Name, Action<AppConfig> Mutate, string Fragment)[] cases =
		[
			("port 下界", c => c.Port = 0, "port=0"),
			("port 上界", c => c.Port = 65536, "port=65536"),
			("bind_family 非法", c => c.BindFamily = "bogus", "bind_family"),
			("delay_ms 非法", c => c.DelayMs = 0, "delay_ms=0"),
			("line_length 下界", c => c.LineLength = 2, "line_length=2"),
			("line_length 上界", c => c.LineLength = 256, "line_length=256"),
			("max_clients 非法", c => c.MaxClients = 0, "max_clients=0"),
			("log_level 非法", c => c.LogLevelName = "warn", "log_level"),
			("enabled 缺凭据", c => c.Notifications.Telegram.Enabled = true, "requires non-empty bot_token and chat_id"),
			("enabled 缺 chat_id", c =>
			{
				c.Notifications.Telegram.Enabled = true;
				c.Notifications.Telegram.BotToken = "T";
			}, "requires non-empty bot_token and chat_id"),
			("dedup 负数", c => c.Notifications.DedupPerIpSeconds = -1, "dedup_per_ip_seconds=-1"),
			("max_per_minute 非法", c => c.Notifications.MaxPerMinute = 0, "max_per_minute=0"),
		];

		foreach ((string name, Action<AppConfig> mutate, string fragment) in cases)
		{
			AppConfig config = ValidConfig();
			mutate(config);
			List<string> errors = config.Validate();
			Assert.AreEqual(1, errors.Count, $"「{name}」应恰好报一条错误，实际：{string.Join(" | ", errors)}");
			StringAssert.Contains(errors[0], fragment, $"「{name}」错误内容");
		}

		// enabled=true 且凭据齐全 → 合法
		AppConfig withCredentials = ValidConfig();
		withCredentials.Notifications.Telegram.Enabled = true;
		withCredentials.Notifications.Telegram.BotToken = "T";
		withCredentials.Notifications.Telegram.ChatId = "C";
		Assert.AreEqual(0, withCredentials.Validate().Count, "凭据齐全时 enabled=true 应合法");

		// 组合非法 → 逐项各报一条
		AppConfig combined = ValidConfig();
		combined.Port = 70000;
		combined.LineLength = 1;
		combined.Notifications.DedupPerIpSeconds = -5;
		List<string> combinedErrors = combined.Validate();
		Assert.AreEqual(3, combinedErrors.Count, $"组合非法应报 3 条，实际：{string.Join(" | ", combinedErrors)}");
	}

	/// <summary>写临时配置文件（[TestCleanup] 统一清理）。</summary>
	private string WriteTemp(string json)
	{
		string path = Path.Combine(Path.GetTempPath(), $"sshtw-test-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, json);
		tempFiles.Add(path);
		return path;
	}

	/// <summary>合法基准配置（逐项覆写一个非法值后校验）。</summary>
	private static AppConfig ValidConfig() => new()
	{
		Nickname = "unit",
		Port = 2222,
		BindFamily = "auto",
		DelayMs = 100,
		LineLength = 32,
		MaxClients = 16,
		LogLevelName = "info",
		Notifications = new NotificationConfig
		{
			Telegram = new TelegramConfig { Enabled = false, BotToken = "", ChatId = "" },
			NotifyOnConnect = true,
			NotifyOnClose = true,
			DedupPerIpSeconds = 60,
			MaxPerMinute = 3,
		},
	};

	private static void AssertDefaults(AppConfig config, string scenario)
	{
		Assert.AreEqual("", config.Nickname, scenario);
		Assert.AreEqual(2222, config.Port, scenario);
		Assert.AreEqual("auto", config.BindFamily, scenario);
		Assert.AreEqual(10000, config.DelayMs, scenario);
		Assert.AreEqual(32, config.LineLength, scenario);
		Assert.AreEqual(4096, config.MaxClients, scenario);
		Assert.AreEqual("info", config.LogLevelName, scenario);
		Assert.AreEqual(300, config.Notifications.DedupPerIpSeconds, scenario);
		Assert.AreEqual(3, config.Notifications.MaxPerMinute, scenario);
		Assert.IsTrue(config.Notifications.NotifyOnConnect, scenario);
		Assert.IsTrue(config.Notifications.NotifyOnClose, scenario);
	}
}
