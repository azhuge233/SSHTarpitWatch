using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 配置加载与校验（设计 §4.1 / §4.2 / §7）：全部默认值、宽松 JSON（注释与尾逗号）、
/// 文件缺失 / 非法 JSON / 空文档行为、校验矩阵（非法值各报对应错 + 组合多条）、
/// 严格键校验（未知键报错）、嵌套段缺省值、双关警告。
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
		Assert.AreEqual(0, emptyResult.Warnings.Count, "默认配置不应有警告（两开关不同时为关）");
		AssertDefaults(emptyResult.Config, "空对象");
	}

	[TestMethod]
	public void Load_ExplicitNullSections_FallBackToDefaults()
	{
		// 显式 null 字段/段：Normalize 兜底后同样落回默认值（源生成会把非空属性置 null）
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(
			"""{"nickname": null, "log_level": null, "server": null, "notifications": {"telegram": null, "filter": null, "throttle": null}}"""));

		Assert.AreEqual(0, result.Errors.Count, "显式 null 字段应经 Normalize 兜底");
		AssertDefaults(result.Config, "显式 null 字段");
		Assert.IsNotNull(result.Config.Server, "null 的 server 段应被重建");
		Assert.IsNotNull(result.Config.Notifications.Filter, "null 的 filter 段应被重建");
		Assert.IsNotNull(result.Config.Notifications.Throttle, "null 的 throttle 段应被重建");
	}

	[TestMethod]
	public void Load_LenientJson_CommentsAndTrailingCommasAccepted()
	{
		string json = """
			{
			  // 行注释：监听段
			  "server": {
			    "port": 8080,
			    "delay_ms": 250,
			    "line_length": 64, /* 块注释：行长上限 */
			    "bind_family": "ipv4",
			    "max_clients": 7,
			  },
			  "log_level": "debug",
			  "notifications": {
			    "telegram": { "enabled": true, "bot_token": "T", "chat_id": "C", },
			    "filter": { "on_connect": true, "min_duration_seconds": 30, },
			    "throttle": { "per_ip_seconds": 0, "max_per_minute": 9, },
			  },
			}
			""";

		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(json));

		Assert.AreEqual(0, result.Errors.Count);
		Assert.AreEqual(0, result.Warnings.Count);
		Assert.AreEqual(8080, result.Config.Server.Port);
		Assert.AreEqual(250, result.Config.Server.DelayMs);
		Assert.AreEqual(64, result.Config.Server.LineLength);
		Assert.AreEqual("ipv4", result.Config.Server.BindFamily);
		Assert.AreEqual(7, result.Config.Server.MaxClients);
		Assert.AreEqual("debug", result.Config.LogLevelName);
		Assert.IsTrue(result.Config.Notifications.Telegram.Enabled);
		Assert.AreEqual("T", result.Config.Notifications.Telegram.BotToken);
		Assert.AreEqual("C", result.Config.Notifications.Telegram.ChatId);
		Assert.IsTrue(result.Config.Notifications.Filter.OnConnect);
		Assert.AreEqual(30, result.Config.Notifications.Filter.MinDurationSeconds);
		Assert.AreEqual(0, result.Config.Notifications.Throttle.PerIpSeconds);
		Assert.AreEqual(9, result.Config.Notifications.Throttle.MaxPerMinute);
	}

	[TestMethod]
	public void Load_NestedSections_PartialKeysKeepDefaults()
	{
		// 嵌套段只写部分键：段内其余键与未出现的段都回落默认值（设计 §4.2）
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(
			"""{"server": {"port": 4000}, "notifications": {"filter": {"min_duration_seconds": 60}}}"""));

		Assert.AreEqual(0, result.Errors.Count);
		Assert.AreEqual(4000, result.Config.Server.Port, "显式键生效");
		Assert.AreEqual("auto", result.Config.Server.BindFamily, "同段缺省键回落默认值");
		Assert.AreEqual(10000, result.Config.Server.DelayMs);
		Assert.AreEqual(32, result.Config.Server.LineLength);
		Assert.AreEqual(4096, result.Config.Server.MaxClients);
		Assert.AreEqual(60, result.Config.Notifications.Filter.MinDurationSeconds);
		Assert.IsFalse(result.Config.Notifications.Filter.OnConnect, "on_connect 默认关（R3.3）");
		Assert.IsTrue(result.Config.Notifications.Filter.OnClose);
		Assert.AreEqual(300, result.Config.Notifications.Throttle.PerIpSeconds, "未出现的 throttle 段取默认值");
		Assert.AreEqual(3, result.Config.Notifications.Throttle.MaxPerMinute);
	}

	[TestMethod]
	[DataRow("""{"por": 1}""", "por")]
	[DataRow("""{"server": {"ports": 1}}""", "server.ports")]
	[DataRow("""{"notifications": {"filter": {"min_duration": 5}}}""", "notifications.filter.min_duration")]
	[DataRow("""{"notifications": {"telegram": {"token": "x"}}}""", "notifications.telegram.token")]
	public void Load_UnknownKeys_ReportedAndRejected(string json, string badKey)
	{
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(json));

		Assert.AreEqual(1, result.Errors.Count, $"未知键 {badKey} 应恰好报一条错误：{string.Join(" | ", result.Errors)}");
		StringAssert.Contains(result.Errors[0], badKey, "报错应点名未知键");
	}

	[TestMethod]
	public void Load_MultipleUnknownKeys_ReportedPerKey()
	{
		// 逐条报错：同一份配置里的多个未知键各报各的（不静默忽略）——旧布局的键也按未知键报
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(
			"""{"port": 2222, "typo_key": 1, "notifications": {"dedup_per_ip_seconds": 60, "bogus": true}}"""));

		Assert.AreEqual(4, result.Errors.Count, $"应逐条报 4 条：{string.Join(" | ", result.Errors)}");
		Assert.IsTrue(result.Errors.Any(error => error.Contains("port")), "旧布局的顶层键");
		Assert.IsTrue(result.Errors.Any(error => error.Contains("typo_key")), "拼错的顶层键");
		Assert.IsTrue(result.Errors.Any(error => error.Contains("dedup_per_ip_seconds")), "旧布局的嵌套键");
		Assert.IsTrue(result.Errors.Any(error => error.Contains("notifications.bogus")), "拼错的嵌套键");
	}

	[TestMethod]
	public void KeyValidator_AllowedKeys_MatchConfigModel()
	{
		// 防漂移（评审建议）：允许键集合是模型 [JsonPropertyName] 的手工副本——双向断言，
		// 新增/改名配置键而忘记同步校验器时此用例即红（漏同步要么让合法键被误判，要么让拼错的键静默滑过）。
		Dictionary<string, Type> scopeTypes = new(StringComparer.Ordinal)
		{
			["Root"] = typeof(AppConfig),
			["Server"] = typeof(ServerConfig),
			["Notifications"] = typeof(NotificationConfig),
			["Telegram"] = typeof(TelegramConfig),
			["Filter"] = typeof(FilterConfig),
			["Throttle"] = typeof(ThrottleConfig),
			["Geoip"] = typeof(GeoIpConfig),
		};

		Assert.AreEqual(
			scopeTypes.Count,
			ConfigKeyValidator.allowedKeys.Count,
			"作用域数量不一致：新增/删除作用域时请同步本用例的类型映射与校验器");

		foreach ((ConfigKeyValidator.KeyScope scope, HashSet<string> allowed) in ConfigKeyValidator.allowedKeys)
		{
			Assert.IsTrue(
				scopeTypes.TryGetValue(scope.ToString(), out Type? model),
				$"未知作用域 {scope}——请在本用例补类型映射");
			HashSet<string> modelKeys = model
				.GetProperties()
				.Select(property => System.Reflection.CustomAttributeExtensions
					.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>(property)?.Name)
				.Where(name => name is not null)
				.Select(name => name!)
				.ToHashSet(StringComparer.Ordinal);

			CollectionAssert.AreEquivalent(
				modelKeys.ToArray(),
				allowed.ToArray(),
				$"作用域 {scope} 的允许键与 {model.Name} 的 [JsonPropertyName] 不一致——两侧必须逐键对应");
		}
	}

	[TestMethod]
	public void Load_BothNotificationSwitchesOff_WarnsButLoads()
	{
		// 双关（R4.6）：警告但不报错、不拒绝启动——焦油坑照常工作，只是通知全被丢弃
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(
			"""{"notifications": {"filter": {"on_connect": false, "on_close": false}}}"""));

		Assert.AreEqual(0, result.Errors.Count, "双关不应报错");
		Assert.AreEqual(1, result.Warnings.Count);
		StringAssert.Contains(result.Warnings[0], "notifications inert");
		StringAssert.Contains(result.Warnings[0], "filter.on_connect");
		StringAssert.Contains(result.Warnings[0], "filter.on_close");
		Assert.IsFalse(result.Config.Notifications.Filter.OnClose);
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
			("server.port 下界", c => c.Server.Port = 0, "server.port=0"),
			("server.port 上界", c => c.Server.Port = 65536, "server.port=65536"),
			("server.bind_family 非法", c => c.Server.BindFamily = "bogus", "server.bind_family"),
			("server.delay_ms 非法", c => c.Server.DelayMs = 0, "server.delay_ms=0"),
			("server.line_length 下界", c => c.Server.LineLength = 2, "server.line_length=2"),
			("server.line_length 上界", c => c.Server.LineLength = 256, "server.line_length=256"),
			("server.max_clients 非法", c => c.Server.MaxClients = 0, "server.max_clients=0"),
			("log_level 非法", c => c.LogLevelName = "warn", "log_level"),
			("enabled 缺凭据", c => c.Notifications.Telegram.Enabled = true, "requires non-empty bot_token and chat_id"),
			("enabled 缺 chat_id", c =>
			{
				c.Notifications.Telegram.Enabled = true;
				c.Notifications.Telegram.BotToken = "T";
			}, "requires non-empty bot_token and chat_id"),
			("min_duration_seconds 负数", c => c.Notifications.Filter.MinDurationSeconds = -1,
				"notifications.filter.min_duration_seconds=-1"),
			("per_ip_seconds 负数", c => c.Notifications.Throttle.PerIpSeconds = -1,
				"notifications.throttle.per_ip_seconds=-1"),
			("max_per_minute 非法", c => c.Notifications.Throttle.MaxPerMinute = 0,
				"notifications.throttle.max_per_minute=0"),
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
		combined.Server.Port = 70000;
		combined.Server.LineLength = 1;
		combined.Notifications.Throttle.PerIpSeconds = -5;
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
		LogLevelName = "info",
		Server = new ServerConfig
		{
			Port = 2222,
			BindFamily = "auto",
			DelayMs = 100,
			LineLength = 32,
			MaxClients = 16,
		},
		Notifications = new NotificationConfig
		{
			Telegram = new TelegramConfig { Enabled = false, BotToken = "", ChatId = "" },
			Filter = new FilterConfig { OnConnect = true, OnClose = true, MinDurationSeconds = 0 },
			Throttle = new ThrottleConfig { PerIpSeconds = 60, MaxPerMinute = 3 },
		},
	};

	private static void AssertDefaults(AppConfig config, string scenario)
	{
		Assert.AreEqual("", config.Nickname, scenario);
		Assert.AreEqual("info", config.LogLevelName, scenario);
		Assert.AreEqual(2222, config.Server.Port, scenario);
		Assert.AreEqual("auto", config.Server.BindFamily, scenario);
		Assert.AreEqual(10000, config.Server.DelayMs, scenario);
		Assert.AreEqual(32, config.Server.LineLength, scenario);
		Assert.AreEqual(4096, config.Server.MaxClients, scenario);
		Assert.IsFalse(config.Notifications.Telegram.Enabled, scenario);
		Assert.IsFalse(config.Notifications.Filter.OnConnect, scenario, "默认只在断开时发一条（R3.2/R3.3）");
		Assert.IsTrue(config.Notifications.Filter.OnClose, scenario);
		Assert.AreEqual(0, config.Notifications.Filter.MinDurationSeconds, scenario);
		Assert.AreEqual(300, config.Notifications.Throttle.PerIpSeconds, scenario);
		Assert.AreEqual(3, config.Notifications.Throttle.MaxPerMinute, scenario);
	}

	/// <summary>文件缺失：启动路径行为不变（警告 + 默认值），但要打 FileMissing 标记——重载路径据此拒绝静默回落（设计 §4.3）。</summary>
	[TestMethod]
	public void Load_MissingFile_SetsFileMissingFlag()
	{
		string path = Path.Combine(Path.GetTempPath(), $"sshtw-missing-{Guid.NewGuid():N}.json");

		ConfigLoadResult result = ConfigLoader.Load(path);

		Assert.IsTrue(result.FileMissing, "文件缺失必须打标记（重载路径的判定依据）");
		Assert.AreEqual(0, result.Errors.Count, "缺失不是错误：启动路径用默认值 + 警告（R4.3）");
		Assert.AreEqual(1, result.Warnings.Count, "保留原有警告语义");
		Assert.IsFalse(result.Warnings[0].Contains("inert", StringComparison.Ordinal), "缺失警告与双关警告是两回事");
	}

	/// <summary>开了地区解析却没配库 → 警告但不报错（避免通知里静默持续显示「库缺失」；重载同口径）。</summary>
	[TestMethod]
	public void Load_GeoipEnabledWithoutDatabases_Warns()
	{
		ConfigLoadResult result = ConfigLoader.Load(WriteTemp(
			"""{"notifications": {"geoip": {"enabled": true, "databases": []}}}"""));

		Assert.AreEqual(0, result.Errors.Count, "缺库不是配置错误（运行期降级为「库缺失」）");
		Assert.AreEqual(1, result.Warnings.Count);
		StringAssert.Contains(result.Warnings[0], "no databases configured");
	}

	/// <summary>发布件出厂示例必须能被加载器解析、且键名全部合法——它被 README 指示 `cp` 成 config.json 直接使用。
	/// 唯一允许的报错是刻意保留的凭据闸（fail-loud，防“静默不通知”）。</summary>
	[TestMethod]
	public void ShippedExampleConfig_LoadsWithKeyCleanJson()
	{
		string path = FindRepoFile("config/config.example.json");
		ConfigLoadResult result = ConfigLoader.Load(path);

		Assert.IsTrue(result.Errors.Count <= 1, string.Join(" | ", result.Errors));
		Assert.IsTrue(
			result.Errors.All(e => e.Contains("bot_token", StringComparison.Ordinal)),
			$"出厂示例只允许凭据闸报错，实际: {string.Join(" | ", result.Errors)}");
	}

	/// <summary>从测试输出目录向上找到仓库根（以 SSHTarpitWatch.slnx 为锚）。</summary>
	private static string FindRepoFile(string relative)
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SSHTarpitWatch.slnx")))
		{
			dir = dir.Parent;
		}

		Assert.IsNotNull(dir, "找不到仓库根（SSHTarpitWatch.slnx）");
		return Path.GetFullPath(Path.Combine(dir.FullName, relative));
	}
}
