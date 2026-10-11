using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 地区查询门面（设计 §3.6 / §7）：降级四态（未启用 / 库缺失 / 无结果 / 有结果）、
/// 缓存（命中不重复读、否定结果缓存、上限淘汰、重载清空）、查库异常就地消化、异步门面。
/// </summary>
[TestClass]
public sealed class GeoLookupTests
{
	private TempDirectory? directory;

	[TestInitialize]
	public void Setup() => directory = new TempDirectory();

	[TestCleanup]
	public void Cleanup() => directory?.Dispose();

	[TestMethod]
	public void Lookup_Disabled_NoQueryNoCacheNoWarning()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		AppConfig config = GeoTestSupport.ConfigWithGeoip("city.mmdb");
		config.Notifications.Geoip.Enabled = false;

		using GeoLookup lookup = new(TestSupport.Store(config), directory.Root);
		Assert.AreEqual(0, lookup.Open().Count, "未启用时不检查库文件（不报库缺失警告）");

		GeoLookupResult result = lookup.Lookup("1.2.3.4");

		Assert.AreEqual(GeoStatus.NoDatabase, result.Status);
		Assert.IsNull(result.Result);
		Assert.AreEqual(0, lookup.LookupCount, "未启用不查询");
		Assert.AreEqual(0, lookup.CacheCount, "未启用不缓存");
	}

	[TestMethod]
	public void Lookup_CacheHit_DoesNotQueryAgain()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();

		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode);
		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode);

		Assert.AreEqual(1, lookup.LookupCount, "第二次命中缓存，不重复读库");
		Assert.AreEqual(1, lookup.CacheHits);
		Assert.AreEqual(1, lookup.CacheCount);
	}

	[TestMethod]
	public void Lookup_NegativeResult_IsCached()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();

		Assert.AreEqual(GeoStatus.NoRecord, lookup.Lookup("9.9.9.9").Status);
		Assert.AreEqual(GeoStatus.NoRecord, lookup.Lookup("9.9.9.9").Status);

		Assert.AreEqual(1, lookup.LookupCount, "否定结果同样入缓存（设计 §3.6）");
		Assert.AreEqual(1, lookup.CacheHits);
	}

	[TestMethod]
	public void Lookup_CacheCapacity_EvictsOldest()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		GeoCache cache = new(capacity: 2);
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root, cache);
		lookup.Open();

		lookup.Lookup("1.0.0.1");
		lookup.Lookup("1.0.0.2");
		lookup.Lookup("1.0.0.3"); // 超限 → 按最旧批量淘汰

		Assert.IsTrue(cache.Count <= 2, $"缓存不超过硬上限：{cache.Count}");
		long before = lookup.LookupCount;
		lookup.Lookup("1.0.0.1");
		Assert.AreEqual(before + 1, lookup.LookupCount, "被淘汰的条目应重新查库");
	}

	[TestMethod]
	public void Lookup_Reload_ClearsCache()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();
		lookup.Lookup("1.2.3.4");
		Assert.AreEqual(1, lookup.CacheCount);

		lookup.Reload();

		Assert.AreEqual(0, lookup.CacheCount, "重载清空缓存（换库 / 换语言后旧结果不再成立）");
		lookup.Lookup("1.2.3.4");
		Assert.AreEqual(2, lookup.LookupCount, "清空后重新查库");
	}

	[TestMethod]
	public void Lookup_BrokenDatabaseContent_IsSwallowedAsNoRecord()
	{
		string dbPath = directory!.FilePath("city.mmdb");
		File.WriteAllBytes(dbPath, GeoTestSupport.MmdbBytes("1.2.3.0/24", MmdbFixture.Record(code: "CN")));
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();
		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode);

		// 打开后把内容换成垃圾（同一 inode）：查库异常必须就地消化，绝不冒泡
		File.WriteAllBytes(dbPath, new byte[4096]);
		GeoLookupResult result = lookup.Lookup("5.5.5.5");

		Assert.AreEqual(GeoStatus.NoRecord, result.Status, "损坏读取按无结果处理（debug 日志），不抛异常");
	}

	[TestMethod]
	public async Task LookupAsync_Facade_ReturnsAndCaches()
	{
		directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", GeoTestSupport.FullRecord()));
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();

		GeoLookupResult first = await lookup.LookupAsync("1.2.3.4");
		GeoLookupResult second = await lookup.LookupAsync("1.2.3.4");

		Assert.AreEqual("深圳市", first.Result?.City);
		Assert.AreEqual("深圳市", second.Result?.City);
		Assert.AreEqual(1, lookup.LookupCount, "异步门面同样走缓存");
		Assert.AreEqual(1, lookup.CacheHits);

		AppConfig disabled = GeoTestSupport.ConfigWithGeoip("city.mmdb");
		disabled.Notifications.Geoip.Enabled = false;
		using GeoLookup off = new(TestSupport.Store(disabled), directory.Root);
		Assert.AreEqual(GeoStatus.NoDatabase, (await off.LookupAsync("1.2.3.4")).Status, "未启用时异步门面直接返回");
	}
}
