using System.Text.Json.Serialization;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Config;

/// <summary>
/// 应用配置（键名 snake_case，见设计 §4.2）：顶层 nickname / log_level，其余收进 server 与 notifications 两段。
/// 全部键带默认值：文件缺失或键缺失时按默认值运行。
/// 注意：Validate 之前必须先 Normalize——JSON 里显式写 null 会把非空属性置空。
/// </summary>
internal sealed class AppConfig
{
	[JsonPropertyName("nickname")]
	public string Nickname { get; set; } = "";

	[JsonPropertyName("log_level")]
	public string LogLevelName { get; set; } = "info";

	[JsonPropertyName("server")]
	public ServerConfig Server { get; set; } = new();

	[JsonPropertyName("notifications")]
	public NotificationConfig Notifications { get; set; } = new();

	/// <summary>解析后的日志级别；非法值由 Validate 报错，这里按 info 兜底。</summary>
	[JsonIgnore]
	public LogLevel Level => LogLevelName switch
	{
		"error" => LogLevel.Error,
		"debug" => LogLevel.Debug,
		_ => LogLevel.Info,
	};

	/// <summary>JSON null 兜底（源生成直接赋值，可能把声明为非空的属性置 null）。</summary>
	public void Normalize()
	{
		Nickname ??= "";
		LogLevelName ??= "info";
		Server ??= new ServerConfig();
		Notifications ??= new NotificationConfig();
		Server.Normalize();
		Notifications.Normalize();
	}

	/// <summary>校验矩阵（设计 §4.2）；返回空列表表示通过。</summary>
	public List<string> Validate()
	{
		var errors = new List<string>();
		if (LogLevelName is not ("error" or "info" or "debug"))
		{
			errors.Add($"log_level=\"{LogLevelName}\" must be one of error, info, debug");
		}

		Server.Validate(errors);
		Notifications.Validate(errors);
		return errors;
	}
}
