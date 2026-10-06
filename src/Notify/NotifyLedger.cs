namespace SSHTarpitWatch.Notify;

/// <summary>
/// 去重账本 + 当日计数（设计 §3.2 步 1/2、§3.4）：全部操作在锁内完成（每次判定/发送读当前配置快照）。
/// 内存保护：定期清理（由管道定时器驱动）+ 硬上限（超出按最旧批量淘汰，摊还成本）。
/// </summary>
internal sealed class NotifyLedger
{
	/// <summary>硬上限默认值（设计 §3.4：10 万条）。</summary>
	public const int DefaultCapacity = 100_000;

	private readonly object sync = new();
	private readonly Dictionary<string, DateTimeOffset> dedup = new(StringComparer.Ordinal);
	private readonly Dictionary<string, DailyEntry> daily = new(StringComparer.Ordinal);
	private readonly int capacity;

	public NotifyLedger(int capacity = DefaultCapacity) => this.capacity = capacity;

	/// <summary>去重条目数（测试/诊断用）。</summary>
	public int DedupCount
	{
		get
		{
			lock (sync)
			{
				return dedup.Count;
			}
		}
	}

	/// <summary>当日计数条目数（测试/诊断用）。</summary>
	public int DailyCount
	{
		get
		{
			lock (sync)
			{
				return daily.Count;
			}
		}
	}

	/// <summary>
	/// 去重闸（设计 §3.2 步 2）：该 IP 上次过闸距今 ≥ windowSeconds 才通过；通过即写账本。
	/// windowSeconds = 0 表示关闭去重（恒通过）。
	/// </summary>
	public bool TryPassDedup(string host, DateTimeOffset nowUtc, int windowSeconds)
	{
		lock (sync)
		{
			if (windowSeconds > 0
				&& dedup.TryGetValue(host, out DateTimeOffset last)
				&& nowUtc - last < TimeSpan.FromSeconds(windowSeconds))
			{
				return false;
			}

			MarkPassedCore(host, nowUtc);
			return true;
		}
	}

	/// <summary>写账本（过闸即记账；close 跟随其 connect 过闸时刷新，设计 §3.2 步 2）。</summary>
	public void MarkPassed(string host, DateTimeOffset nowUtc)
	{
		lock (sync)
		{
			MarkPassedCore(host, nowUtc);
		}
	}

	/// <summary>当日计数（设计 §3.2 步 1，始终执行）：按本地日期分桶，返回该 IP 今日第 N 次连接。</summary>
	public int RegisterConnect(string host, DateTimeOffset localNow)
	{
		lock (sync)
		{
			DateOnly today = DateOnly.FromDateTime(localNow.DateTime);
			if (!daily.TryGetValue(host, out DailyEntry entry) || entry.Date != today)
			{
				entry = new DailyEntry(today, 0, localNow);
			}

			entry = entry with { Count = entry.Count + 1, Updated = localNow };
			daily[host] = entry;
			EvictIfNeeded(daily, value => value.Updated);
			return entry.Count;
		}
	}

	/// <summary>定期清理（设计 §3.4，每 10 分钟由管道触发）：去重条目过期即清、计数条目跨日即清。</summary>
	public void Cleanup(DateTimeOffset nowUtc, DateTimeOffset localNow, int dedupWindowSeconds)
	{
		lock (sync)
		{
			TimeSpan window = TimeSpan.FromSeconds(Math.Max(0, dedupWindowSeconds));
			RemoveWhere(dedup, last => dedupWindowSeconds <= 0 || nowUtc - last >= window);
			DateOnly today = DateOnly.FromDateTime(localNow.DateTime);
			RemoveWhere(daily, value => value.Date != today);
		}
	}

	private void MarkPassedCore(string host, DateTimeOffset nowUtc)
	{
		dedup[host] = nowUtc;
		EvictIfNeeded(dedup, value => value);
	}

	/// <summary>硬上限（设计 §3.4）：达到上限时按最旧批量清到 90%，摊还单次成本。</summary>
	private void EvictIfNeeded<T>(Dictionary<string, T> map, Func<T, DateTimeOffset> updatedAt)
	{
		if (map.Count < capacity)
		{
			return;
		}

		int target = capacity - Math.Max(1, capacity / 10);
		string[] doomed = map
			.OrderBy(pair => updatedAt(pair.Value))
			.Take(Math.Max(1, map.Count - target))
			.Select(pair => pair.Key)
			.ToArray();
		foreach (string key in doomed)
		{
			map.Remove(key);
		}
	}

	private static void RemoveWhere<T>(Dictionary<string, T> map, Func<T, bool> predicate)
	{
		List<string>? doomed = null;
		foreach ((string key, T value) in map)
		{
			if (predicate(value))
			{
				(doomed ??= []).Add(key);
			}
		}

		if (doomed is null)
		{
			return;
		}

		foreach (string key in doomed)
		{
			map.Remove(key);
		}
	}

	/// <summary>当日计数条目：本地日期 + 次数 + 最近更新（更新时刻用于最旧淘汰）。</summary>
	private readonly record struct DailyEntry(DateOnly Date, int Count, DateTimeOffset Updated);
}
