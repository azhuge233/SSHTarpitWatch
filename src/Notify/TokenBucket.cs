namespace SSHTarpitWatch.Notify;

/// <summary>
/// 全局限速闸（设计 §3.2 步 3）：令牌桶，容量 = max_per_minute；连续补充（速率 = max_per_minute/60 每秒，按小数累计）；
/// connect / close 两类事件共用同一只桶；容量按当前配置快照传入（热重载即时生效）。
/// </summary>
internal sealed class TokenBucket
{
	private readonly TimeProvider timeProvider;
	private readonly object sync = new();
	private double tokens;
	private long lastTimestamp;
	private int capacity = -1;

	public TokenBucket(TimeProvider timeProvider) => this.timeProvider = timeProvider;

	/// <summary>尝试取用 1 个令牌；不足则返回 false（调用方记 suppressed_rate）。</summary>
	public bool TryConsume(int capacityPerMinute)
	{
		lock (sync)
		{
			long now = timeProvider.GetTimestamp();
			int currentCapacity = Math.Max(1, capacityPerMinute);
			if (capacity < 0)
			{
				// 初始满桶：允许一小段突发（容量 = max_per_minute）
				capacity = currentCapacity;
				tokens = currentCapacity;
			}
			else
			{
				double elapsedSeconds = timeProvider.GetElapsedTime(lastTimestamp, now).TotalSeconds;
				capacity = currentCapacity;
				tokens = Math.Min(capacity, tokens + Math.Max(0, elapsedSeconds) * capacity / 60.0);
			}

			lastTimestamp = now;
			if (tokens < 1.0)
			{
				return false;
			}

			tokens -= 1.0;
			return true;
		}
	}
}
