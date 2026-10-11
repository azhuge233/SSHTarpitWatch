using System.Net;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// 库列表注册表（设计 §3.6）：按配置顺序持有已打开的读取器，查询时逐库问、逐字段合并
/// （库顺序 = 配置顺序 = 优先级）；SIGHUP 重载时比对文件 mtime，变化则重开（失败保留旧读取器 + ERROR 日志）。
/// 单个库打不开 / 查询出错都不影响其他库；查询与重载共用一把锁（库列表在重载时整体替换，
/// 锁保证在途查询不会读到已释放的读取器；查库本身很快，锁竞争可忽略）。
/// </summary>
internal sealed class GeoRegistry : IDisposable
{
	private readonly object sync = new();
	private readonly string baseDirectory;
	private Entry[] databases = [];
	private long lookups;

	/// <param name="baseDirectory">配置文件所在目录：databases 相对路径的基准（设计 §3.6 / R5.5）。</param>
	public GeoRegistry(string baseDirectory) => this.baseDirectory = baseDirectory;

	/// <summary>当前可用库数（诊断用）。</summary>
	public int OpenCount
	{
		get
		{
			lock (sync)
			{
				return databases.Length;
			}
		}
	}

	/// <summary>实际查库次数（测试 / 诊断用）：缓存命中不经过这里。</summary>
	public long LookupCount => Interlocked.Read(ref lookups);

	/// <summary>
	/// 启动路径：按配置打开全部库；返回不可读库的路径清单（调用方打 CONFIG warning，不报错退出——
	/// 设计 §4.1 / §3.6）。enabled=false 时不打开也不报告。
	/// </summary>
	public IReadOnlyList<string> Open(AppConfig config)
	{
		lock (sync)
		{
			CloseAll();
			List<string> problems = [];
			if (!config.Notifications.Geoip.Enabled)
			{
				return problems;
			}

			List<Entry> opened = [];
			foreach (string path in ResolvePaths(config))
			{
				if (TryOpenEntry(path, out Entry entry, out _))
				{
					opened.Add(entry);
				}
				else
				{
					problems.Add(path);
				}
			}

			databases = [.. opened];
			return problems;
		}
	}

	/// <summary>
	/// 热重载（设计 §4.3 / §3.6）：列表增删照办；同路径比对文件 mtime，变化则重开——
	/// 重开失败保留旧读取器 + ERROR 日志（进程不退出）；enabled=false 时不动库列表。
	/// </summary>
	public void Reload(AppConfig config)
	{
		lock (sync)
		{
			if (!config.Notifications.Geoip.Enabled)
			{
				// 关掉地区解析即释放库句柄（重新开启时按需重开）；缓存由 GeoLookup.Reload 清空
				CloseAll();
				return;
			}

			Dictionary<string, Entry> previous = new(StringComparer.Ordinal);
			foreach (Entry entry in databases)
			{
				previous[entry.Path] = entry;
			}

			List<Entry> next = [];
			foreach (string path in ResolvePaths(config))
			{
				if (!previous.TryGetValue(path, out Entry? old))
				{
					if (TryOpenEntry(path, out Entry added, out string addError))
					{
						next.Add(added);
					}
					else
					{
						Log.Error($"GEO reload failed: {path}: {addError}; database skipped");
					}

					continue;
				}

				previous.Remove(path);

				if (LastWriteUtc(path) == old.Mtime)
				{
					next.Add(old);
					continue;
				}

				if (TryOpenEntry(path, out Entry reopened, out string error))
				{
					old.Reader.Dispose();
					next.Add(reopened);
				}
				else
				{
					Log.Error($"GEO reload failed: {path}: {error}; keeping the previous reader");
					next.Add(old);
				}
			}

			foreach (Entry removed in previous.Values)
			{
				removed.Reader.Dispose(); // 配置里删掉的库
			}

			databases = [.. next];
			Log.Debug($"GEO reloaded databases={databases.Length}");
		}
	}

	/// <summary>
	/// 逐库查询并逐字段合并（第一个非空值胜出）；单个库查询失败 = 该库本轮缺席（debug 日志），
	/// 其余库照常；全部字段为空 → NoRecord；没有任何可用库 → DbMissing。
	/// </summary>
	public GeoLookupResult Lookup(IPAddress address, string language)
	{
		lock (sync)
		{
			Interlocked.Increment(ref lookups);
			if (databases.Length == 0)
			{
				return GeoLookupResult.Missing;
			}

			string country = "", code = "", region = "", city = "", isp = "";
			List<string>? sources = null;
			foreach (Entry entry in databases)
			{
				if (!entry.Reader.CanLookup(address))
				{
					continue;
				}

				GeoResult? result;
				try
				{
					result = entry.Reader.Lookup(address, language);
				}
				catch (Exception ex)
				{
					// 查库异常一律就地消化：单个库坏 / 坏数据 = 该库本轮缺席，不影响其他库
					Log.Debug($"GEO lookup failed path={entry.Path}: {ex.GetType().Name}: {ex.Message}");
					continue;
				}

				if (result is null)
				{
					continue;
				}

				bool contributed = false;
				contributed |= Fill(ref country, result.Country);
				contributed |= Fill(ref code, result.CountryCode);
				contributed |= Fill(ref region, result.Region);
				contributed |= Fill(ref city, result.City);
				contributed |= Fill(ref isp, result.Isp);
				if (contributed)
				{
					(sources ??= []).Add(result.Source);
				}
			}

			GeoResult merged = new(country, code, region, city, isp, sources is null ? "" : string.Join("+", sources));
			return merged.IsEmpty ? GeoLookupResult.NoRecord : GeoLookupResult.Found(merged);
		}
	}

	public void Dispose()
	{
		lock (sync)
		{
			CloseAll();
		}
	}

	/// <summary>逐字段取第一个非空值：目标已有值或新值为空 → 不动；返回是否填入了新值。</summary>
	private static bool Fill(ref string field, string value)
	{
		if (field.Length > 0 || value.Length == 0)
		{
			return false;
		}

		field = value;
		return true;
	}

	/// <summary>databases 的相对路径以配置文件所在目录为基准（设计 §3.6 / R5.5）；保序去重。</summary>
	private IReadOnlyList<string> ResolvePaths(AppConfig config)
	{
		List<string> paths = [];
		foreach (string configured in config.Notifications.Geoip.Databases)
		{
			if (configured.Length == 0)
			{
				continue;
			}

			string path = Path.IsPathRooted(configured) ? configured : Path.Combine(baseDirectory, configured);
			if (!paths.Contains(path, StringComparer.Ordinal))
			{
				paths.Add(path);
			}
		}

		return paths;
	}

	private static bool TryOpenEntry(string path, out Entry entry, out string error)
	{
		entry = null!;
		IGeoDatabaseReader? reader;
		try
		{
			reader = GeoDatabase.TryOpen(path, out error);
		}
		catch (Exception ex)
		{
			// 打开路径兜底（评审 #7-1）：解析型异常（InvalidDataException 等）不在读取器的过滤器内，
			// 库文件被非原子替换（cp 先截断后写入）时会在 GetLength 与读取之间逃逸，崩掉启动或 SIGHUP
			// ——一律降级为「库不可读」（设计 §4.1 / §4.3：不报错退出）
			reader = null;
			error = $"{ex.GetType().Name}: {ex.Message}";
		}

		if (reader is null)
		{
			return false;
		}

		entry = new Entry(path, LastWriteUtc(path), reader);
		Log.Debug($"GEO database={path} kind={reader.Kind}");
		return true;
	}

	/// <summary>文件最后写入时刻（UTC）；缺失 / 不可访问返回 DateTime.MinValue（重载时按「变了」处理）。</summary>
	private static DateTime LastWriteUtc(string path)
	{
		try
		{
			return File.GetLastWriteTimeUtc(path);
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
		{
			return DateTime.MinValue;
		}
	}

	private void CloseAll()
	{
		foreach (Entry entry in databases)
		{
			entry.Reader.Dispose();
		}

		databases = [];
	}

	/// <summary>一个已打开的库：路径（相对路径已解析）、打开时的 mtime、读取器。</summary>
	private sealed record Entry(string Path, DateTime Mtime, IGeoDatabaseReader Reader);
}
