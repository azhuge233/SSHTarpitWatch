using SSHTarpitWatch.Config;
using SSHTarpitWatch.Notify;

namespace SSHTarpitWatch.Tests;

/// <summary>测试用通知渠道：记录文本与调用次数，结果可按调用序号脚本化；用于驱动管道各闸门。</summary>
internal sealed class FakeNotifier : INotifier
{
	private readonly object sync = new();
	private readonly List<string> texts = [];
	private int calls;

	/// <summary>渠道闸覆盖值；null = 跟随配置的 telegram.enabled（与真实实现一致）。</summary>
	public bool? EnabledOverride { get; set; }

	/// <summary>按调用序号（1 起）决定结果；未设置 = 成功。</summary>
	public Func<int, NotifySendResult>? Responder { get; set; }

	/// <summary>每次调用时回调（测试可用来记录时刻/唤醒等待方）。</summary>
	public Action<int>? OnCall { get; set; }

	/// <summary>首次发送阻塞于此（队满/排空测试用）；完成该任务即放行。</summary>
	public TaskCompletionSource? BlockFirstCall { get; set; }

	public int CallCount => Volatile.Read(ref calls);

	public IReadOnlyList<string> Texts
	{
		get
		{
			lock (sync)
			{
				return texts.ToArray();
			}
		}
	}

	public bool IsEnabled(NotificationConfig config) => EnabledOverride ?? config.Telegram.Enabled;

	public async Task<NotifySendResult> SendAsync(
		string text, NotificationConfig config, CancellationToken cancellationToken)
	{
		int index = Interlocked.Increment(ref calls);
		lock (sync)
		{
			texts.Add(text);
		}

		OnCall?.Invoke(index);
		if (index == 1 && BlockFirstCall is TaskCompletionSource blocker)
		{
			await blocker.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		return Responder?.Invoke(index) ?? NotifySendResult.Ok();
	}
}
