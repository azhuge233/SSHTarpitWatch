using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 格式嗅探与库注册表（设计 §3.6 / §7）：MMDB / xdb / 两者都不是；多库逐字段合并（顺序 = 优先级）；
/// 单库坏不影响其他库；相对路径以基准目录解析；SIGHUP 重载（mtime 变化重开 / 失败保留旧读取器）。
/// </summary>
[TestClass]
public sealed class GeoRegistryTests
{
	private TempDirectory? directory;

	[TestInitialize]
	public void Setup() => directory = new TempDirectory();

	[TestCleanup]
	public void Cleanup() => directory?.Dispose();

	[TestMethod]
	public void GeoDatabase_Sniff_RecognizesMmdbXdbAndNeither()
	{
		string mmdbPath = directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.2.3.0/24", MmdbFixture.Record(code: "CN")));
		string xdbPath = directory.Write("ip2region.xdb", GeoTestSupport.XdbBytes("1.2.3.0", "1.2.3.255", "中国|0|0|电信|0"));
		string junkPath = directory.Write("junk.bin", new byte[4096]);

		using IGeoDatabaseReader? mmdb = GeoDatabase.TryOpen(mmdbPath, out string mmdbError);
		Assert.IsNotNull(mmdb, mmdbError);
		Assert.AreEqual("mmdb", mmdb.Kind);

		using IGeoDatabaseReader? xdb = GeoDatabase.TryOpen(xdbPath, out string xdbError);
		Assert.IsNotNull(xdb, xdbError);
		Assert.AreEqual("xdb", xdb.Kind);

		Assert.IsNull(GeoDatabase.TryOpen(junkPath, out string junkError), "两者都不是 → 该库不可用");
		StringAssert.Contains(junkError, "mmdb");
		StringAssert.Contains(junkError, "xdb");
	}

	[TestMethod]
	public void Registry_MergesPerField_FirstNonEmptyWins_InConfigOrder()
	{
		// 库 A（mmdb）：国家码 + 国家名；库 B（xdb）：省 / 市 / ISP
		directory!.Write("a.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(
			code: "CN",
			countryNames: new Dictionary<string, string> { ["zh"] = "中国" })));
		directory.Write("b.xdb", GeoTestSupport.XdbBytes("1.2.3.0", "1.2.3.255", "0|广东省|深圳市|电信|0"));

		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("a.mmdb", "b.xdb")), directory.Root);
		Assert.AreEqual(0, lookup.Open().Count, "两个库都可读");
		Assert.AreEqual(2, lookup.OpenCount);

		GeoLookupResult result = lookup.Lookup("1.2.3.4");

		Assert.AreEqual(GeoStatus.Ok, result.Status);
		Assert.AreEqual("中国", result.Result?.Country, "国家名来自库 A");
		Assert.AreEqual("CN", result.Result?.CountryCode);
		Assert.AreEqual("广东省", result.Result?.Region, "省来自库 B");
		Assert.AreEqual("深圳市", result.Result?.City);
		Assert.AreEqual("电信", result.Result?.Isp);
		Assert.AreEqual("a.mmdb+b.xdb", result.Result?.Source);
	}

	[TestMethod]
	public void Registry_OrderIsPriority_SameFieldFirstWins()
	{
		directory!.Write("cn.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		directory.Write("us.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "US")));

		using (GeoLookup first = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("cn.mmdb", "us.mmdb")), directory.Root))
		{
			first.Open();
			Assert.AreEqual("CN", first.Lookup("1.2.3.4").Result?.CountryCode, "配置顺序即优先级");
		}

		using GeoLookup second = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("us.mmdb", "cn.mmdb")), directory.Root);
		second.Open();
		Assert.AreEqual("US", second.Lookup("1.2.3.4").Result?.CountryCode, "换顺序换优先级");
	}

	[TestMethod]
	public void Registry_AsnOnlyDatabase_CountsAsFound_AndMergesIspAsnAcrossDatabases()
	{
		// 只有号的库（GeoLite2-ASN 形态）：算「有结果」（Ok），不是 NoRecord
		directory!.Write("asn.mmdb", GeoTestSupport.MmdbBytes("8.8.8.0/24", new Dictionary<string, object>
		{
			["autonomous_system_number"] = 15169u,
		}));
		using (GeoLookup numberOnly = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("asn.mmdb")), directory.Root))
		{
			numberOnly.Open();
			GeoLookupResult result = numberOnly.Lookup("8.8.8.8");
			Assert.AreEqual(GeoStatus.Ok, result.Status, "只有 ASN 的库同样算查到结果");
			Assert.AreEqual("15169", result.Result?.Asn);
			Assert.AreEqual("", result.Result?.Isp, "只有号 → 组织名为空");
		}

		// 名与号跨库分别拼合（逐字段取首个非空）：号来自库 A、组织名与地区来自库 B
		directory.Write("info.mmdb", GeoTestSupport.MmdbBytes("8.8.8.0/24", new Dictionary<string, object>
		{
			["country"] = "United States",
			["country_code"] = "US",
			["as_name"] = "Google LLC",
		}));
		using GeoLookup merged = new(
			TestSupport.Store(GeoTestSupport.ConfigWithGeoip("asn.mmdb", "info.mmdb")), directory.Root);
		merged.Open();
		GeoLookupResult both = merged.Lookup("8.8.8.8");

		Assert.AreEqual(GeoStatus.Ok, both.Status);
		Assert.AreEqual("15169", both.Result?.Asn, "号来自库 A");
		Assert.AreEqual("Google LLC", both.Result?.Isp, "组织名来自库 B");
		Assert.AreEqual("United States", both.Result?.Country);
		Assert.AreEqual("US", both.Result?.CountryCode);
		Assert.AreEqual("asn.mmdb+info.mmdb", both.Result?.Source);
	}

	[TestMethod]
	public void Registry_BrokenDatabase_DoesNotAffectOthers()
	{
		directory!.Write("good.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		directory.Write("broken.mmdb", new byte[512]);

		using GeoLookup lookup = new(
			TestSupport.Store(GeoTestSupport.ConfigWithGeoip("missing.mmdb", "broken.mmdb", "good.mmdb")), directory.Root);
		IReadOnlyList<string> problems = lookup.Open();

		Assert.AreEqual(2, problems.Count, "缺失文件与坏文件各报一条（Program 据此打 CONFIG warning）");
		Assert.AreEqual(1, lookup.OpenCount, "可用库只有一个");
		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode, "单库坏不影响可用库出结果");
	}

	[TestMethod]
	public void Registry_NoUsableDatabase_ReturnsDbMissing()
	{
		using GeoLookup empty = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip()), directory!.Root);
		Assert.AreEqual(GeoStatus.DbMissing, empty.Lookup("1.2.3.4").Status, "enabled=true 但未配库 → 库缺失");

		using GeoLookup missing = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("missing.mmdb")), directory.Root);
		Assert.AreEqual(1, missing.Open().Count);
		Assert.AreEqual(GeoStatus.DbMissing, missing.Lookup("1.2.3.4").Status, "库文件不可读 → 库缺失");
	}

	[TestMethod]
	public void Registry_RelativeAndAbsolutePaths_ResolvedAgainstBaseDirectory()
	{
		string absolute = directory!.Write("city.mmdb", GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));

		using (GeoLookup relative = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root))
		{
			relative.Open();
			Assert.AreEqual(1, relative.OpenCount, "相对路径以基准目录（配置文件所在目录）解析");
			Assert.AreEqual("CN", relative.Lookup("1.2.3.4").Result?.CountryCode);
		}

		// 绝对路径与基准目录无关（故意用一个不含该文件的基准目录）
		using GeoLookup direct = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip(absolute)), AppContext.BaseDirectory);
		direct.Open();
		Assert.AreEqual(1, direct.OpenCount);
		Assert.AreEqual("CN", direct.Lookup("1.2.3.4").Result?.CountryCode);
	}

	[TestMethod]
	public void Registry_Reload_ReopensChangedDatabase()
	{
		string dbPath = directory!.FilePath("city.mmdb");
		File.WriteAllBytes(dbPath, GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));

		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();
		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode);

		// 换库（并显式改动 mtime，避免同秒写入的时钟精度问题）→ SIGHUP 重载后生效
		File.WriteAllBytes(dbPath, GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "US")));
		File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow.AddSeconds(5));
		lookup.Reload();

		Assert.AreEqual("US", lookup.Lookup("1.2.3.4").Result?.CountryCode, "mtime 变化 → 重开生效");
	}

	[TestMethod]
	public void Registry_Reload_FailureKeepsPreviousReader()
	{
		string dbPath = directory!.FilePath("city.mmdb");
		File.WriteAllBytes(dbPath, GeoTestSupport.MmdbBytes("1.0.0.0/8", MmdbFixture.Record(code: "CN")));
		using ConsoleCapture log = new();
		using GeoLookup lookup = new(TestSupport.Store(GeoTestSupport.ConfigWithGeoip("city.mmdb")), directory.Root);
		lookup.Open();

		// 库文件消失（句柄以 FileShare.Delete 打开：删除后旧读取器仍可读）
		File.Delete(dbPath);
		lookup.Reload();

		StringAssert.Contains(log.Text, "GEO reload failed", "重开失败记 ERROR");
		StringAssert.Contains(log.Text, "keeping the previous reader", "保留旧读取器（进程不退出）");
		Assert.AreEqual("CN", lookup.Lookup("1.2.3.4").Result?.CountryCode, "旧读取器仍在服务");
	}
}
