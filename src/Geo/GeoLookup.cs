using System.Net;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// 地区查询的异步门面（设计 §3.6）：连接时调用——enabled=false 直接返回（不查询、不缓存）；
/// 否则先查缓存（命中不读库文件、不走线程池），未命中在线程池上查库；结果（含否定项）入缓存。
/// 任何异常就地消化（debug 日志 + 无结果）——绝不冒泡到困住逻辑与通知管道；查库异常不入缓存
/// （避免把瞬时错误固化成长期否定结果）。
/// </summary>
internal sealed class GeoLookup : IDisposable
{
	private readonly ConfigStore configStore;
	private readonly GeoRegistry registry;
	private readonly GeoCache cache;
	private int generation;

	/// <param name="baseDirectory">配置文件所在目录：databases 相对路径的基准（设计 §3.6 / R5.5）。</param>
	public GeoLookup(ConfigStore configStore, string baseDirectory, GeoCache? cache = null)
	{
		this.configStore = configStore;
		registry = new GeoRegistry(baseDirectory);
		this.cache = cache ?? new GeoCache();
	}

	/// <summary>当前可用库数（诊断用）。</summary>
	public int OpenCount => registry.OpenCount;

	/// <summary>实际查库次数（测试 / 诊断用）：缓存命中不增加。</summary>
	public long LookupCount => registry.LookupCount;

	/// <summary>缓存条目数（测试 / 诊断用）。</summary>
	public int CacheCount => cache.Count;

	/// <summary>缓存命中次数（测试 / 诊断用）。</summary>
	public long CacheHits => cache.Hits;

	/// <summary>启动装配：按配置打开库；返回不可读库的路径清单（Program 据此打 CONFIG warning，不报错退出）。</summary>
	public IReadOnlyList<string> Open() => registry.Open(configStore.Current);

	/// <summary>热重载：库列表同步（mtime 变化则重开；失败保留旧读取器）+ 清缓存（换库 / 换语言后旧结果不再成立）。</summary>
	public void Reload()
	{
		// 先换代：在途查询（旧库 / 旧语言）的结果不再入缓存——清空与换代之间的窗口由此闭合（评审 #7-2）
		Interlocked.Increment(ref generation);
		cache.Clear();
		registry.Reload(configStore.Current);
	}

	/// <summary>连接期异步查询（与读 banner 同时机，设计 §3.6）：未启用 / 缓存命中时不走线程池。</summary>
	public ValueTask<GeoLookupResult> LookupAsync(string host)
	{
		GeoIpConfig geoip = configStore.Current.Notifications.Geoip;
		if (!geoip.Enabled)
		{
			return ValueTask.FromResult(GeoLookupResult.Disabled);
		}

		if (cache.TryGet(host, out GeoLookupResult cached))
		{
			return ValueTask.FromResult(cached);
		}

		int started = Volatile.Read(ref generation);
		return new ValueTask<GeoLookupResult>(Task.Run(() => LookupCore(host, geoip, started)));
	}

	/// <summary>同步查询（测试 / 内部用）。</summary>
	public GeoLookupResult Lookup(string host)
	{
		GeoIpConfig geoip = configStore.Current.Notifications.Geoip;
		if (!geoip.Enabled)
		{
			return GeoLookupResult.Disabled;
		}

		return cache.TryGet(host, out GeoLookupResult cached)
			? cached
			: LookupCore(host, geoip, Volatile.Read(ref generation));
	}

	public void Dispose() => registry.Dispose();

	private GeoLookupResult LookupCore(string host, GeoIpConfig geoip, int startedGeneration)
	{
		GeoLookupResult result;
		try
		{
			if (!IPAddress.TryParse(host, out IPAddress? address))
			{
				return GeoLookupResult.NoRecord; // 非 IP（正常路径不会出现）：不缓存
			}

			result = registry.Lookup(address, geoip.Language);
		}
		catch (Exception ex)
		{
			// 查库异常就地消化：debug 日志 + 无结果，绝不冒泡到困住逻辑与通知管道
			Log.Debug($"GEO lookup error host={host}: {ex.GetType().Name}: {ex.Message}");
			return GeoLookupResult.NoRecord;
		}

		if (startedGeneration == Volatile.Read(ref generation))
		{
			cache.Set(host, result); // 换代（SIGHUP 换库 / 换语言）后，在途旧结果一律丢弃
		}

		Log.Debug($"GEO host={host} status={result.Status} fields={Summarize(result)}");
		return result;
	}

	/// <summary>查询结果的 debug 摘要（排障用）。</summary>
	private static string Summarize(GeoLookupResult result) => result.Result is GeoResult found
		? $"country={found.Country} code={found.CountryCode} region={found.Region} city={found.City} "
			+ $"isp={found.Isp} asn={found.Asn} source={found.Source}"
		: "-";
}
