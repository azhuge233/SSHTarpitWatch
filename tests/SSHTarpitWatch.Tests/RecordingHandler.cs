using System.Net;
using System.Net.Http;

namespace SSHTarpitWatch.Tests;

/// <summary>一次被记录的 HTTP 请求（URL + 请求体）。</summary>
internal sealed record RecordedRequest(string Url, string Body);

/// <summary>测试用 HttpMessageHandler（设计 §3.5 测试缝）：记录请求，可按脚本返回响应/抛异常。</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
	private readonly object sync = new();
	private readonly List<RecordedRequest> requests = [];

	public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

	public int RequestCount
	{
		get
		{
			lock (sync)
			{
				return requests.Count;
			}
		}
	}

	public IReadOnlyList<RecordedRequest> Requests
	{
		get
		{
			lock (sync)
			{
				return requests.ToArray();
			}
		}
	}

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string body = request.Content is null
			? ""
			: await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		lock (sync)
		{
			requests.Add(new RecordedRequest(request.RequestUri?.ToString() ?? "", body));
		}

		Func<HttpRequestMessage, HttpResponseMessage>? responder = Responder;
		return responder is null ? new HttpResponseMessage(HttpStatusCode.OK) : responder(request);
	}
}
