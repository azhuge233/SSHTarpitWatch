using System.Text.Json;

namespace SSHTarpitWatch.Config;

/// <summary>
/// 配置加载结果：Errors 非空表示不可用（启动路径报错退出，重载路径保留旧配置）；
/// Warnings 为可继续运行但需提示的情况（例如文件缺失、双关警告）。
/// FileMissing：文件不存在的专用标记——重载路径据此拒绝「回落默认值」（设计 §4.3：静默回落会关掉通知、还可能改端口）。
/// </summary>
internal sealed record ConfigLoadResult(AppConfig Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, bool FileMissing = false);

/// <summary>
/// 配置加载与校验（设计 §4.1）：默认路径由 CLI 层给出（= 二进制所在目录的 config.json），--config 覆盖；
/// 文件缺失用默认值 + 警告；不可读（权限等）报错——不静默回落；
/// 解析失败、键名错误（旧键名/未知键）与取值校验失败一律报错（退出码 1）。
/// </summary>
internal static class ConfigLoader
{
	public static ConfigLoadResult Load(string path)
	{
		var errors = new List<string>();
		var warnings = new List<string>();

		string json;
		try
		{
			// 严格键校验与反序列化都要看全文，一次读入（配置文件很小）
			json = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			// 路径形态非法（CLI 传入的 --config 含非法字符等）也走「清晰报错 + 退出码 1」，不抛未处理异常。
			// 文件缺失 → 默认值 + 警告；其余读取失败（权限不足等）→ 报错——静默回落默认值会关掉通知，属危险行为。
			// 启动路径由入口报错退出；重载路径保留旧配置。
			if (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				warnings.Add($"config file not found: {path}; running with defaults (notifications disabled)");
				return new ConfigLoadResult(new AppConfig(), errors, warnings, FileMissing: true);
			}

			errors.Add($"{path}: cannot read config file: {ex.Message}");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}

		AppConfig? config;
		List<string> keyErrors;
		try
		{
			// 键名先校验：未知键会被源生成读取静默忽略（拼错键名会静默关掉通知，R4.5）
			keyErrors = ConfigKeyValidator.Validate(json);
			config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
		}
		catch (JsonException ex)
		{
			errors.Add($"{path}: {ex.Message}");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}

		if (config is null)
		{
			errors.Add($"{path}: JSON document is empty");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}

		errors.AddRange(keyErrors);
		config.Normalize();
		errors.AddRange(config.Validate());
		if (errors.Count == 0 && IsInert(config))
		{
			// 双关警告（R4.6）：合法状态——焦油坑照常工作，只是通知全被丢弃；不报错、不拒绝启动
			warnings.Add("notifications inert: both filter.on_connect and filter.on_close are false");
		}

		return new ConfigLoadResult(config, errors, warnings);
	}

	/// <summary>两个通知开关同时关闭（设计 §4.1 / R4.6）。</summary>
	private static bool IsInert(AppConfig config) =>
		!config.Notifications.Filter.OnConnect && !config.Notifications.Filter.OnClose;
}
