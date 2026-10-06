using System.Text.Json;

namespace SSHTarpitWatch.Config;

/// <summary>
/// 配置加载结果：Errors 非空表示不可用（启动路径报错退出，重载路径保留旧配置）；
/// Warnings 为可继续运行但需提示的情况（例如文件缺失）。
/// </summary>
internal sealed record ConfigLoadResult(AppConfig Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>配置加载与校验（设计 §4.1）：默认路径由 CLI 层给出（= 二进制所在目录的 config.json），--config 覆盖；文件缺失用默认值 + 警告；不可读（权限等）报错——不静默回落。</summary>
internal static class ConfigLoader
{
	public static ConfigLoadResult Load(string path)
	{
		var errors = new List<string>();
		var warnings = new List<string>();

		AppConfig? config;
		try
		{
			using FileStream stream = File.OpenRead(path);
			config = JsonSerializer.Deserialize(stream, ConfigJsonContext.Default.AppConfig);
		}
		catch (JsonException ex)
		{
			errors.Add($"{path}: {ex.Message}");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 文件缺失 → 默认值 + 警告；其余读取失败（权限不足等）→ 报错——静默回落默认值会关掉通知，属危险行为。
			// 启动路径由入口报错退出；重载路径保留旧配置。
			if (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				warnings.Add($"config file not found: {path}; running with defaults (notifications disabled)");
				return new ConfigLoadResult(new AppConfig(), errors, warnings);
			}

			errors.Add($"{path}: cannot read config file: {ex.Message}");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}

		if (config is null)
		{
			errors.Add($"{path}: JSON document is empty");
			return new ConfigLoadResult(new AppConfig(), errors, warnings);
		}

		config.Normalize();
		errors.AddRange(config.Validate());
		return new ConfigLoadResult(config, errors, warnings);
	}
}
