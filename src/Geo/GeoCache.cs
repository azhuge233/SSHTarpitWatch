namespace SSHTarpitWatch.Geo;

/// <summary>
/// 按 IP 的地区查询缓存（设计 §3.4 / §3.6）：硬上限 20000 条 + 最旧淘汰（超出时按写入顺序批量清到 90%，
/// 摊还单次成本，与去重账本同口径），含「无结果」「库缺失」的否定项；随进程存活，SIGHUP 重载时清空
/// （换库 / 换语言后旧结果不再成立）。全部操作在锁内完成；命中即不读库文件。
/// </summary>
internal sealed class GeoCache
{
	/// <summary>硬上限（设计 §3.4：20000 条，约 1MB）。</summary>
	public const int DefaultCapacity = 20_000;

	private readonly object sync = new();
	private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	private readonly int capacity;
	private long sequence;
	private long hits;

	/// <param name="capacity">硬上限（测试注入小值以覆盖淘汰路径）。</param>
	public GeoCache(int capacity = DefaultCapacity) => this.capacity = Math.Max(1, capacity);

	/// <summary>缓存条目数（测试 / 诊断用）。</summary>
	public int Count
	{
		get
		{
			lock (sync)
			{
				return entries.Count;
			}
		}
	}

	/// <summary>命中次数（测试 / 诊断用）——命中即不读库文件。</summary>
	public long Hits => Interlocked.Read(ref hits);

	/// <summary>查缓存；命中返回 true（结果可能是否定项：无结果 / 库缺失）。</summary>
	public bool TryGet(string host, out GeoLookupResult result)
	{
		lock (sync)
		{
			if (entries.TryGetValue(host, out Entry entry))
			{
				hits++; // 锁内自增；Hits 的 Interlocked.Read 保证读侧一致
				result = entry.Result;
				return true;
			}
		}

		result = default;
		return false;
	}

	/// <summary>写缓存（同 IP 覆盖旧值）。</summary>
	public void Set(string host, GeoLookupResult result)
	{
		lock (sync)
		{
			entries[host] = new Entry(result, ++sequence);
			if (entries.Count > capacity)
			{
				EvictOldest();
			}
		}
	}

	/// <summary>清空（SIGHUP 重载 / 换库后调用）。</summary>
	public void Clear()
	{
		lock (sync)
		{
			entries.Clear();
		}
	}

	/// <summary>硬上限：超出时按最旧（写入序号最小）批量清到 90%。</summary>
	private void EvictOldest()
	{
		int target = capacity - Math.Max(1, capacity / 10);
		string[] doomed = entries
			.OrderBy(pair => pair.Value.Sequence)
			.Take(Math.Max(1, entries.Count - target))
			.Select(pair => pair.Key)
			.ToArray();
		foreach (string key in doomed)
		{
			entries.Remove(key);
		}
	}

	/// <summary>缓存条目：结果 + 写入序号（淘汰按最旧）。</summary>
	private readonly record struct Entry(GeoLookupResult Result, long Sequence);
}
