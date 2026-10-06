using System.Text.Json.Serialization;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Config;

/// <summary>绑定地址族（config 的 bind_family）。</summary>
internal enum BindFamilyMode
{
	Auto,
	Ipv4,
	Ipv6,
}

/// <summary>
/// 应用配置（键名 snake_case，见设计 §4.2）。全部键带默认值：文件缺失或键缺失时按默认值运行。
/// 注意：Validate 之前必须先 Normalize——JSON 里显式写 null 会把非空属性置空。
/// </summary>
internal sealed class AppConfig
{
	[JsonPropertyName("nickname")]
	public string Nickname { get; set; } = "";

	[JsonPropertyName("port")]
	public int Port { get; set; } = 2222;

	[JsonPropertyName("bind_family")]
	public string BindFamily { get; set; } = "auto";

	[JsonPropertyName("delay_ms")]
	public int DelayMs { get; set; } = 10000;

	[JsonPropertyName("line_length")]
	public int LineLength { get; set; } = 32;

	[JsonPropertyName("max_clients")]
	public int MaxClients { get; set; } = 4096;

	[JsonPropertyName("log_level")]
	public string LogLevelName { get; set; } = "info";

	[JsonPropertyName("notifications")]
	public NotificationConfig Notifications { get; set; } = new();

	/// <summary>解析后的绑定地址族；非法值由 Validate 报错，这里按 auto 兜底。</summary>
	[JsonIgnore]
	public BindFamilyMode FamilyMode => BindFamily switch
	{
		"ipv4" => BindFamilyMode.Ipv4,
		"ipv6" => BindFamilyMode.Ipv6,
		_ => BindFamilyMode.Auto,
	};

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
		BindFamily ??= "auto";
		LogLevelName ??= "info";
		Notifications ??= new NotificationConfig();
		Notifications.Telegram ??= new TelegramConfig();
		Notifications.Telegram.BotToken ??= "";
		Notifications.Telegram.ChatId ??= "";
	}

	/// <summary>校验矩阵（设计 §4.2）；返回空列表表示通过。</summary>
	public List<string> Validate()
	{
		var errors = new List<string>();
		if (Port is < 1 or > 65535)
		{
			errors.Add($"port={Port} is out of range 1..65535");
		}

		if (BindFamily is not ("auto" or "ipv4" or "ipv6"))
		{
			errors.Add($"bind_family=\"{BindFamily}\" must be one of auto, ipv4, ipv6");
		}

		if (DelayMs < 1)
		{
			errors.Add($"delay_ms={DelayMs} must be >= 1");
		}

		if (LineLength is < 3 or > 255)
		{
			errors.Add($"line_length={LineLength} is out of range 3..255");
		}

		if (MaxClients < 1)
		{
			errors.Add($"max_clients={MaxClients} must be >= 1");
		}

		if (LogLevelName is not ("error" or "info" or "debug"))
		{
			errors.Add($"log_level=\"{LogLevelName}\" must be one of error, info, debug");
		}

		NotificationConfig notifications = Notifications;
		bool hasCredentials = notifications.Telegram.BotToken.Length > 0 && notifications.Telegram.ChatId.Length > 0;
		if (notifications.Telegram.Enabled && !hasCredentials)
		{
			errors.Add("notifications.telegram.enabled=true requires non-empty bot_token and chat_id");
		}

		if (notifications.DedupPerIpSeconds < 0)
		{
			errors.Add($"notifications.dedup_per_ip_seconds={notifications.DedupPerIpSeconds} must be >= 0");
		}

		if (notifications.MaxPerMinute < 1)
		{
			errors.Add($"notifications.max_per_minute={notifications.MaxPerMinute} must be >= 1");
		}

		return errors;
	}
}
