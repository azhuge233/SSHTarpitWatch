using System.Text;
using System.Text.Json;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// Telegram 渠道（设计 §3.2 步 5 / §3.3 / §3.5）：HTTPS POST sendMessage（parse_mode 恒为 HTML——
/// 消息里的动态值由渲染侧转义，见 §3.3/§3.6）；HTTP 超时 10s；429 解析 retry_after 交回管道按重试次数等待重试；
/// 错误信息永不包含 bot_token（§9）。拒收兜底：实体/解析类 400 → 去标记 + 反转义后不带 parse_mode 重发一次
/// （重发仍失败按原失败路径回管道；日志规则不变）。
/// 测试缝：可注入 <see cref="HttpMessageHandler"/> 与 API 基址（默认 https://api.telegram.org），
/// 生产恒为默认值——对外配置表不新增键（§4.2 不变）。
/// </summary>
internal sealed class TelegramNotifier : INotifier, IDisposable
{
	public const string DefaultApiBase = "https://api.telegram.org";

	private const int timeoutSeconds = 10;
	private const string htmlParseMode = "HTML";

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
		string chatId = config.Telegram.ChatId;
		string url = $"{apiBase}/bot{token}/sendMessage";

		Attempt first = await PostAsync(url, chatId, text, htmlParseMode, token, cancellationToken).ConfigureAwait(false);
		if (first.Result.Success || !first.EntityParseRejected)
		{
			return first.Result;
		}

		// 兜底（设计 §3.3）：Telegram 拒收 HTML（实体 / 解析类 400）→ 去掉 code 标记 + 反转义后
		// 不带 parse_mode 重发一次；重发结果原样返回（仍失败则照旧走管道的重试与失败记账）
		Log.Debug("NOTIFY telegram rejected html payload; resending as plain text");
		Attempt retry = await PostAsync(url, chatId, ToPlainText(text), parseMode: null, token, cancellationToken)
			.ConfigureAwait(false);
		return retry.Result;
	}

	public void Dispose() => client.Dispose();

	/// <summary>单次 POST：序列化载荷（parseMode 为 null 时该键不出现在 JSON 里）→ 发送。</summary>
	private async Task<Attempt> PostAsync(
		string url, string chatId, string text, string? parseMode, string token, CancellationToken cancellationToken)
	{
		TelegramSendMessage payload = new() { ChatId = chatId, Text = text, ParseMode = parseMode };
		string json = JsonSerializer.Serialize(payload, NotifyJsonContext.Default.TelegramSendMessage);
		using StringContent content = new(json, Encoding.UTF8, "application/json");
		return await SendCoreAsync(url, content, token, cancellationToken).ConfigureAwait(false);
	}

	private async Task<Attempt> SendCoreAsync(
		string url, HttpContent content, string token, CancellationToken cancellationToken)
	{
		try
		{
			using HttpResponseMessage response = await client.PostAsync(url, content, cancellationToken)
				.ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (response.IsSuccessStatusCode)
			{
				return new Attempt(NotifySendResult.Ok(), false);
			}

			TelegramApiResponse? api = TryParseApiResponse(body);
			string reason = api?.Description ?? response.ReasonPhrase ?? response.StatusCode.ToString();
			return new Attempt(
				NotifySendResult.Fail(
					Sanitize($"HTTP {(int)response.StatusCode}: {reason}", token), ParseRetryAfter(api)),
				(int)response.StatusCode == 400 && IsEntityParseError(reason));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw; // 调用方取消（进程收尾）
		}
		catch (OperationCanceledException)
		{
			return new Attempt(NotifySendResult.Fail($"timeout after {timeoutSeconds}s"), false);
		}
		catch (Exception ex)
		{
			return new Attempt(NotifySendResult.Fail(Sanitize($"{ex.GetType().Name}: {ex.Message}", token)), false);
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

	/// <summary>实体 / 解析类拒收判据（设计 §3.3 兜底）——如 "Bad Request: can't parse entities: …"。</summary>
	private static bool IsEntityParseError(string description) =>
		description.Contains("parse", StringComparison.OrdinalIgnoreCase)
		|| description.Contains("entit", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 去标记 + 反转义（设计 §3.3 兜底）：精确移除我们自己插入的 &lt;code&gt; / &lt;/code&gt;（动态值里的尖括号
	/// 已被转义，故这两个串只可能是标记），再还原 &amp;lt; / &amp;gt; / &amp;amp;（&amp;amp; 最后，避免二次反转义）。
	/// </summary>
	private static string ToPlainText(string text) => text
		.Replace("<code>", "", StringComparison.Ordinal)
		.Replace("</code>", "", StringComparison.Ordinal)
		.Replace("&lt;", "<", StringComparison.Ordinal)
		.Replace("&gt;", ">", StringComparison.Ordinal)
		.Replace("&amp;", "&", StringComparison.Ordinal);

	/// <summary>错误信息脱敏（设计 §9）：token 若出现在任何错误文本中一律替换。</summary>
	private static string Sanitize(string message, string token) =>
		token.Length > 0 && message.Contains(token, StringComparison.Ordinal)
			? message.Replace(token, "<hidden>", StringComparison.Ordinal)
			: message;

	/// <summary>单次 POST 的结果 + 是否属「HTML 实体 / 解析类拒收」（决定是否降级重发）。</summary>
	private readonly record struct Attempt(NotifySendResult Result, bool EntityParseRejected);
}
