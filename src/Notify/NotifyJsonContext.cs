using System.Text.Json.Serialization;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// Telegram 请求/响应 JSON 上下文——System.Text.Json 源生成（NativeAOT 安全，禁反射序列化）。
/// 只覆盖 notify 子系统自己的载荷类型；配置侧见 <c>ConfigJsonContext</c>。
/// </summary>
[JsonSerializable(typeof(TelegramSendMessage))]
[JsonSerializable(typeof(TelegramApiResponse))]
internal sealed partial class NotifyJsonContext : JsonSerializerContext
{
}

/// <summary>sendMessage 请求体（设计 §3.2 步 5）。</summary>
internal sealed class TelegramSendMessage
{
	[JsonPropertyName("chat_id")]
	public string ChatId { get; set; } = "";

	[JsonPropertyName("text")]
	public string Text { get; set; } = "";
}

/// <summary>Telegram API 响应：失败时用于提取 description 与 retry_after。</summary>
internal sealed class TelegramApiResponse
{
	[JsonPropertyName("ok")]
	public bool Ok { get; set; }

	[JsonPropertyName("description")]
	public string? Description { get; set; }

	[JsonPropertyName("parameters")]
	public TelegramResponseParameters? Parameters { get; set; }
}

/// <summary>429 响应里的 parameters（设计 §3.2 步 5）。</summary>
internal sealed class TelegramResponseParameters
{
	[JsonPropertyName("retry_after")]
	public int? RetryAfter { get; set; }
}
