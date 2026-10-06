using System.Text;
using System.Text.Json;
using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// Telegram 渠道（设计 §3.2 步 5 / §3.5）：HTTPS POST sendMessage；HTTP 超时 10s；
/// 429 解析 retry_after 交回管道按重试次数等待重试；错误信息永不包含 bot_token（§9）。
/// 测试缝：可注入 <see cref="HttpMessageHandler"/> 与 API 基址（默认 https://api.telegram.org），
/// 生产恒为默认值——对外配置表不新增键（§4.2 不变）。
/// </summary>
internal sealed class TelegramNotifier : INotifier, IDisposable
{
	public const string DefaultApiBase = "https://api.telegram.org";

	private const int timeoutSeconds = 10;

	private readonly string apiBase;
	private readonly HttpClient client;

	public TelegramNotifier(string apiBase = DefaultApiBase, HttpMessageHandler? handler = null)
	{
		this.apiBase = apiBase.TrimEnd('/');
		client = handler is null ? new HttpClient() : new HttpClient(handler);
		client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
	}

	public bool IsEnabled(NotificationConfig config) => config.Telegram.Enabled;

	public async Task<NotifySendResult> SendAsync(
		string text, NotificationConfig config, CancellationToken cancellationToken)
	{
		string token = config.Telegram.BotToken;
		string url = $"{apiBase}/bot{token}/sendMessage";
		TelegramSendMessage payload = new() { ChatId = config.Telegram.ChatId, Text = text };
		string json = JsonSerializer.Serialize(payload, NotifyJsonContext.Default.TelegramSendMessage);
		using StringContent content = new(json, Encoding.UTF8, "application/json");
		return await SendCoreAsync(url, content, token, cancellationToken).ConfigureAwait(false);
	}

	public void Dispose() => client.Dispose();

	private async Task<NotifySendResult> SendCoreAsync(
		string url, HttpContent content, string token, CancellationToken cancellationToken)
	{
		try
		{
			using HttpResponseMessage response = await client.PostAsync(url, content, cancellationToken)
				.ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (response.IsSuccessStatusCode)
			{
				return NotifySendResult.Ok();
			}

			TelegramApiResponse? api = TryParseApiResponse(body);
			string reason = api?.Description ?? response.ReasonPhrase ?? response.StatusCode.ToString();
			return NotifySendResult.Fail(
				Sanitize($"HTTP {(int)response.StatusCode}: {reason}", token),
				ParseRetryAfter(api));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw; // 调用方取消（进程收尾）
		}
		catch (OperationCanceledException)
		{
			return NotifySendResult.Fail($"timeout after {timeoutSeconds}s");
		}
		catch (Exception ex)
		{
			return NotifySendResult.Fail(Sanitize($"{ex.GetType().Name}: {ex.Message}", token));
		}
	}

	private static TelegramApiResponse? TryParseApiResponse(string body)
	{
		try
		{
			return JsonSerializer.Deserialize(body, NotifyJsonContext.Default.TelegramApiResponse);
		}
		catch (JsonException)
		{
			return null; // 非 JSON 响应体：仅用状态码描述
		}
	}

	private static TimeSpan? ParseRetryAfter(TelegramApiResponse? api) =>
		api?.Parameters?.RetryAfter is int seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null;

	/// <summary>错误信息脱敏（设计 §9）：token 若出现在任何错误文本中一律替换。</summary>
	private static string Sanitize(string message, string token) =>
		token.Length > 0 && message.Contains(token, StringComparison.Ordinal)
			? message.Replace(token, "<hidden>", StringComparison.Ordinal)
			: message;
}
