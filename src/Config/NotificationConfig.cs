using System.Text.Json.Serialization;

namespace SSHTarpitWatch.Config;

/// <summary>
/// 通知配置（设计 §4.2 的 notifications 段）：telegram（渠道）/ filter（开关与时长闸）/ throttle（去重与限速）。
/// 全部键带默认值：段缺失或键缺失时按默认值运行。
/// 注意：Validate 之前必须先 Normalize——JSON 里显式写 null 会把非空属性置空。
/// </summary>
internal sealed class NotificationConfig
{
	[JsonPropertyName("telegram")]
	public TelegramConfig Telegram { get; set; } = new();

	[JsonPropertyName("filter")]
	public FilterConfig Filter { get; set; } = new();

	[JsonPropertyName("throttle")]
	public ThrottleConfig Throttle { get; set; } = new();

	[JsonPropertyName("geoip")]
	public GeoIpConfig Geoip { get; set; } = new();

	/// <summary>JSON null 兜底（源生成直接赋值，可能把声明为非空的属性置 null）。</summary>
	public void Normalize()
	{
		Telegram ??= new TelegramConfig();
		Filter ??= new FilterConfig();
		Throttle ??= new ThrottleConfig();
		Geoip ??= new GeoIpConfig();
		Telegram.Normalize();
		Geoip.Normalize();
	}

	/// <summary>校验矩阵（设计 §4.2）；错误追加进 <paramref name="errors"/>。</summary>
	public void Validate(List<string> errors)
	{
		Telegram.Validate(errors);
		Filter.Validate(errors);
		Throttle.Validate(errors);
		Geoip.Validate(errors);
	}
}

/// <summary>Telegram 渠道配置；bot_token 为机密，日志中永不打印。</summary>
internal sealed class TelegramConfig
{
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; }

	[JsonPropertyName("bot_token")]
	public string BotToken { get; set; } = "";

	[JsonPropertyName("chat_id")]
	public string ChatId { get; set; } = "";

	/// <summary>JSON null 兜底。</summary>
	public void Normalize()
	{
		BotToken ??= "";
		ChatId ??= "";
	}

	/// <summary>渠道开启时凭据必须齐全（设计 §4.1）。</summary>
	public void Validate(List<string> errors)
	{
		if (Enabled && (BotToken.Length == 0 || ChatId.Length == 0))
		{
			errors.Add("notifications.telegram.enabled=true requires non-empty bot_token and chat_id");
		}
	}
}

/// <summary>通知开关与时长闸（设计 §4.2 的 notifications.filter 段）。</summary>
internal sealed class FilterConfig
{
	/// <summary>连接条开关：默认关——默认只在断开时发一条（R3.2 / R3.3）。</summary>
	[JsonPropertyName("on_connect")]
	public bool OnConnect { get; set; }

	/// <summary>断开条开关。</summary>
	[JsonPropertyName("on_close")]
	public bool OnClose { get; set; } = true;

	/// <summary>时长闸（仅断开条）：停留 &lt; 该值的连接不通知，0 = 关（R3.4）。</summary>
	[JsonPropertyName("min_duration_seconds")]
	public int MinDurationSeconds { get; set; }

	/// <summary>校验矩阵（设计 §4.2）。</summary>
	public void Validate(List<string> errors)
	{
		if (MinDurationSeconds < 0)
		{
			errors.Add($"notifications.filter.min_duration_seconds={MinDurationSeconds} must be >= 0");
		}
	}
}

/// <summary>
/// 地区解析（设计 §4.2 的 notifications.geoip 段 / §3.6）：enabled 开关 + 库文件列表（顺序 = 优先级，
/// 相对路径以配置文件所在目录为基准）+ 地区名语言。库文件由用户自备（不随发布件分发）。
/// </summary>
internal sealed class GeoIpConfig
{
	/// <summary>地区解析开关；默认关（关闭时不查询、通知里整行不出现）。</summary>
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; }

	/// <summary>库文件路径列表（顺序 = 优先级）；相对路径以配置文件所在目录为基准（R5.5）。</summary>
	[JsonPropertyName("databases")]
	public string[] Databases { get; set; } = [];

	/// <summary>地区名语言（默认 zh）；库缺该语言时回退英文（R5.6）。</summary>
	[JsonPropertyName("language")]
	public string Language { get; set; } = "zh";

	/// <summary>JSON null 兜底（源生成直接赋值，可能把声明为非空的属性置 null）。</summary>
	public void Normalize()
	{
		Databases ??= [];
		Language = (Language ?? "zh").Trim();
	}

	/// <summary>校验矩阵（设计 §4.2）：库列表里的空路径是配置笔误——报错退出优于静默跳过。</summary>
	public void Validate(List<string> errors)
	{
		if (Databases.Any(string.IsNullOrWhiteSpace))
		{
			errors.Add("notifications.geoip.databases contains an empty path");
		}
	}
}

/// <summary>去重与限速（设计 §4.2 的 notifications.throttle 段）。</summary>
internal sealed class ThrottleConfig
{
	/// <summary>每 IP 冷却秒数；0 = 关闭（R3.6）。</summary>
	[JsonPropertyName("per_ip_seconds")]
	public int PerIpSeconds { get; set; } = 300;

	/// <summary>全局限速：每分钟条数（R3.6）。</summary>
	[JsonPropertyName("max_per_minute")]
	public int MaxPerMinute { get; set; } = 3;

	/// <summary>校验矩阵（设计 §4.2）。</summary>
	public void Validate(List<string> errors)
	{
		if (PerIpSeconds < 0)
		{
			errors.Add($"notifications.throttle.per_ip_seconds={PerIpSeconds} must be >= 0");
		}

		if (MaxPerMinute < 1)
		{
			errors.Add($"notifications.throttle.max_per_minute={MaxPerMinute} must be >= 1");
		}
	}
}
