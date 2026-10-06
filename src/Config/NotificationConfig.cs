using System.Text.Json.Serialization;

namespace SSHTarpitWatch.Config;

/// <summary>通知配置（设计 §4.2）：本批只解析与校验，真正消费在通知批次。</summary>
internal sealed class NotificationConfig
{
	[JsonPropertyName("telegram")]
	public TelegramConfig Telegram { get; set; } = new();

	[JsonPropertyName("notify_on_connect")]
	public bool NotifyOnConnect { get; set; } = true;

	[JsonPropertyName("notify_on_close")]
	public bool NotifyOnClose { get; set; } = true;

	[JsonPropertyName("dedup_per_ip_seconds")]
	public int DedupPerIpSeconds { get; set; } = 300;

	[JsonPropertyName("max_per_minute")]
	public int MaxPerMinute { get; set; } = 3;
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
}
