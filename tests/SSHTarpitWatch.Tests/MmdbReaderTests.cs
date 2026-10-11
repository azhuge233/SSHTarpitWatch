using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// MMDB 读取器（设计 §3.6 / §7）：合成夹具（自造，不引第三方测试数据）——v4/v6 查询、稀疏记录、
/// 24/28/32 位记录、语言回退（zh → zh-CN → en）、指针去重、损坏文件 / 坏元数据 / 资源上限。
/// </summary>
[TestClass]
public sealed class MmdbReaderTests
{
	private TempDirectory? directory;
	private string path = "";

	[TestInitialize]
	public void Setup()
	{
		directory = new TempDirectory();
		path = directory.FilePath("test.mmdb");
	}

	[TestCleanup]
	public void Cleanup() => directory?.Dispose();

	[TestMethod]
	public void Mmdb_V4Lookup_ReturnsExtractedFields()
	{
		MmdbFixture fixture = new();
		fixture.Add("1.2.3.0/24", GeoTestSupport.FullRecord());
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? result = reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh");

		Assert.IsNotNull(result);
		Assert.AreEqual("中国", result.Country);
		Assert.AreEqual("CN", result.CountryCode);
		Assert.AreEqual("广东省", result.Region);
		Assert.AreEqual("深圳市", result.City);
		Assert.AreEqual("Chinanet", result.Isp);
		Assert.AreEqual("test.mmdb", result.Source);
		Assert.AreEqual("mmdb", reader.Kind);
	}

	[TestMethod]
	public void Mmdb_SparseRecord_AndUnknownAddress()
	{
		MmdbFixture fixture = new();
		fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN")); // 只有国家码的稀疏记录
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? sparse = reader.Lookup(IPAddress.Parse("1.2.3.9"), "zh");
		Assert.IsNotNull(sparse, "稀疏记录仍算查到结果（国家码非空）");
		Assert.AreEqual("CN", sparse.CountryCode);
		Assert.AreEqual("", sparse.Country);
		Assert.IsTrue(sparse.HasRegion, "国家码即地区信息");

		Assert.IsNull(reader.Lookup(IPAddress.Parse("9.9.9.9"), "zh"), "库外地址应无记录");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("1.2.4.1"), "zh"), "相邻 /24 之外应无记录");
	}

	[TestMethod]
	public void Mmdb_RecordSizes_AllDecode()
	{
		foreach (int recordSize in new[] { 24, 28, 32 })
		{
			MmdbFixture fixture = new() { RecordSize = recordSize };
			fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN", cityNames: new Dictionary<string, string> { ["zh"] = "深圳市" }));
			fixture.Add("5.0.0.0/8", MmdbFixture.Record(code: "US", cityNames: new Dictionary<string, string> { ["en"] = "New York" }));
			using IGeoDatabaseReader reader = Open(fixture);

			GeoResult? first = reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh");
			Assert.AreEqual("CN", first?.CountryCode, $"record_size={recordSize}");
			Assert.AreEqual("深圳市", first?.City, $"record_size={recordSize}");
			Assert.AreEqual("US", reader.Lookup(IPAddress.Parse("5.6.7.8"), "zh")?.CountryCode, $"record_size={recordSize}");
			Assert.AreEqual("US", reader.Lookup(IPAddress.Parse("5.255.255.255"), "zh")?.CountryCode, $"record_size={recordSize}");
			Assert.IsNull(reader.Lookup(IPAddress.Parse("9.9.9.9"), "zh"), $"record_size={recordSize}：库外地址无记录");
		}
	}

	[TestMethod]
	public void Mmdb_V6Database_AnswersV4AndV6()
	{
		MmdbFixture fixture = new() { IpVersion = 6 };
		fixture.Add("::1.2.3.0/120", MmdbFixture.Record(code: "CN", countryNames: new Dictionary<string, string> { ["zh"] = "中国" }));
		fixture.Add("::ffff:5.6.7.0/120", MmdbFixture.Record(code: "US", countryNames: new Dictionary<string, string> { ["zh"] = "美国" }));
		fixture.Add("2001:db8::/32", MmdbFixture.Record(code: "JP", countryNames: new Dictionary<string, string> { ["zh"] = "日本" }));
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.AreEqual("CN", reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh")?.CountryCode, "v4 → 高 96 位全 0 的 ::/96（规范位置）");
		Assert.AreEqual("US", reader.Lookup(IPAddress.Parse("5.6.7.8"), "zh")?.CountryCode, "v4 → ::ffff/96（别名兜底）");
		Assert.AreEqual("JP", reader.Lookup(IPAddress.Parse("2001:db8::1"), "zh")?.CountryCode, "v6 地址直接查询");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("9.9.9.9"), "zh"), "v4 库外地址无记录");
		Assert.IsTrue(reader.CanLookup(IPAddress.Parse("1.2.3.4")), "v6 库覆盖 v4");
		Assert.IsTrue(reader.CanLookup(IPAddress.Parse("2001:db8::1")));
	}

	[TestMethod]
	public void Mmdb_V4Database_SkipsV6Address()
	{
		MmdbFixture fixture = new();
		fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN"));
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.IsFalse(reader.CanLookup(IPAddress.Parse("2001:db8::1")), "v4 库不答 v6");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("2001:db8::1"), "zh"));
	}

	[TestMethod]
	public void Mmdb_PointerFixture_DecodesSharedStrings()
	{
		Dictionary<string, string> names = new() { ["zh"] = "中国" };
		MmdbFixture fixture = new() { ShareStrings = true };
		fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN", countryNames: names));
		fixture.Add("5.6.7.0/24", MmdbFixture.Record(code: "CN", countryNames: names));
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.AreEqual("中国", reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh")?.Country, "第一份记录内联");
		Assert.AreEqual("中国", reader.Lookup(IPAddress.Parse("5.6.7.8"), "zh")?.Country, "第二份记录的重复字符串写成指针，解引用后同值");
	}

	[TestMethod]
	public void Mmdb_TopLevelAsnRecord_IspForms()
	{
		// GeoLite2-ASN 系：顶层 autonomous_system_number / autonomous_system_organization（无 country / city / traits）
		MmdbFixture fixture = new();
		fixture.Add("8.8.8.0/24", new Dictionary<string, object>
		{
			["autonomous_system_number"] = 15169u,
			["autonomous_system_organization"] = "Google LLC",
		});
		fixture.Add("9.9.9.0/24", new Dictionary<string, object>
		{
			["autonomous_system_number"] = 3356u,
		});
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? full = reader.Lookup(IPAddress.Parse("8.8.8.8"), "en");
		Assert.IsNotNull(full, "只有 ISP/ASN 字段同样算查到结果");
		Assert.AreEqual("Google LLC", full.Isp, "顶层 autonomous_system_organization");
		Assert.AreEqual("", full.Country);
		Assert.IsFalse(full.HasRegion, "ASN 库不提供地区字段");

		Assert.AreEqual("AS3356", reader.Lookup(IPAddress.Parse("9.9.9.9"), "en")?.Isp, "只有 ASN 号 → 退化为 AS{号}");
	}

	[TestMethod]
	public void Mmdb_IpinfoLite_AsNameAndAsnString()
	{
		// IPinfo Lite：记录为扁平字符串键（真实库实测序）——country / country_code / as_name / asn；as_domain、continent 忽略
		MmdbFixture fixture = new();
		fixture.Add("8.8.8.0/24", new Dictionary<string, object>
		{
			["country"] = "United States",
			["country_code"] = "US",
			["continent"] = "North America",
			["continent_code"] = "NA",
			["as_name"] = "Google LLC",
			["asn"] = "AS15169",
			["as_domain"] = "google.com",
		});
		fixture.Add("1.1.1.0/24", new Dictionary<string, object>
		{
			["asn"] = "AS13335",
			["as_domain"] = "cloudflare.com",
		});
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? named = reader.Lookup(IPAddress.Parse("8.8.8.8"), "en");
		Assert.AreEqual("Google LLC", named?.Isp, "as_name 即组织名（与地区字段同记录共存）");
		Assert.AreEqual("United States", named?.Country);
		Assert.AreEqual("US", named?.CountryCode);

		Assert.AreEqual("AS13335", reader.Lookup(IPAddress.Parse("1.1.1.1"), "en")?.Isp, "缺 as_name → 退化用 asn 字符串");
	}

	[TestMethod]
	public void Mmdb_LanguageSelection_ExactPrefixedEnglish()
	{
		MmdbFixture fixture = new();
		fixture.Add("1.0.0.0/8", MmdbFixture.Record(code: "CN", countryNames: new Dictionary<string, string>
		{
			["zh"] = "中国",
			["en"] = "China",
		}));
		fixture.Add("2.0.0.0/8", MmdbFixture.Record(code: "US", countryNames: new Dictionary<string, string> { ["en"] = "United States" }));
		fixture.Add("3.0.0.0/8", MmdbFixture.Record(code: "JP", countryNames: new Dictionary<string, string>
		{
			["zh-CN"] = "日本",
			["en"] = "Japan",
		}));
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.AreEqual("中国", reader.Lookup(IPAddress.Parse("1.1.1.1"), "zh")?.Country, "精确语言命中");
		Assert.AreEqual("China", reader.Lookup(IPAddress.Parse("1.1.1.1"), "en")?.Country);
		Assert.AreEqual("United States", reader.Lookup(IPAddress.Parse("2.2.2.2"), "zh")?.Country, "缺 zh 回退 en");
		Assert.AreEqual("日本", reader.Lookup(IPAddress.Parse("3.3.3.3"), "zh")?.Country, "zh 命中 zh-CN 前缀（GeoLite2/IPinfo 的中文名即 zh-CN）");
	}

	[TestMethod]
	public void Mmdb_MissingMarkerOrTruncated_OpenFails()
	{
		File.WriteAllBytes(path, new byte[64]); // 既无 MMDB 标记也不是 xdb
		IGeoDatabaseReader? garbage = GeoDatabase.TryOpen(path, out string garbageError);
		Assert.IsNull(garbage, "垃圾文件不应被识别为库");
		StringAssert.Contains(garbageError, "marker not found");
		StringAssert.Contains(garbageError, "xdb");

		MmdbFixture fixture = new();
		fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN"));
		byte[] valid = fixture.ToBytes();
		File.WriteAllBytes(path, valid[..(valid.Length - 64)]); // 元数据被截断
		Assert.IsNull(GeoDatabase.TryOpen(path, out _), "截断文件不应打开");

		File.WriteAllBytes(path, []);
		Assert.IsNull(GeoDatabase.TryOpen(path, out _), "空文件不应打开");
	}

	[TestMethod]
	public void Mmdb_BadMetadata_OpenFails()
	{
		(MmdbFixture Fixture, string Fragment)[] cases =
		[
			(BuildBroken(metadata => metadata.Remove("node_count")), "node_count"),
			(BuildBroken(metadata => metadata["record_size"] = (ushort)27), "record_size"),
			(BuildBroken(metadata => metadata["ip_version"] = (ushort)5), "ip_version"),
			(BuildBroken(metadata => metadata.Remove("database_type")), "database_type"),
		];

		foreach ((MmdbFixture fixture, string fragment) in cases)
		{
			IGeoDatabaseReader? reader = TryOpen(fixture, out string error);
			Assert.IsNull(reader, $"坏元数据（{fragment}）应拒绝打开");
			StringAssert.Contains(error, fragment, $"报错应点名问题字段：{error}");
		}

		static MmdbFixture BuildBroken(Action<Dictionary<string, object>> mutate)
		{
			MmdbFixture fixture = new() { MutateMetadata = mutate };
			fixture.Add("1.2.3.0/24", MmdbFixture.Record(code: "CN"));
			return fixture;
		}
	}

	[TestMethod]
	public void Mmdb_DeepNesting_ExceedsDepthLimit()
	{
		// 记录里挂 600 层嵌套（未知键 → 跳过时遍历）→ 超过默认深度上限 512，查库报错
		// （异常由 GeoLookup / 注册表就地消化：本条只验证读取器不返回部分结果）
		object nested = "leaf";
		for (int i = 0; i < 600; i++)
		{
			nested = new Dictionary<string, object> { ["x"] = nested };
		}

		Dictionary<string, object> record = MmdbFixture.Record(code: "CN");
		record["junk"] = nested;
		MmdbFixture fixture = new();
		fixture.Add("1.2.3.0/24", record);
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.ThrowsExactly<InvalidDataException>(() => reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh"));
	}

	/// <summary>写夹具并打开（要求成功）。</summary>
	private IGeoDatabaseReader Open(MmdbFixture fixture)
	{
		File.WriteAllBytes(path, fixture.ToBytes());
		IGeoDatabaseReader? reader = GeoDatabase.TryOpen(path, out string error);
		Assert.IsNotNull(reader, $"夹具应能打开：{error}");
		return reader;
	}

	/// <summary>写夹具并尝试打开（损坏用例用）。</summary>
	private IGeoDatabaseReader? TryOpen(MmdbFixture fixture, out string error)
	{
		File.WriteAllBytes(path, fixture.ToBytes());
		return GeoDatabase.TryOpen(path, out error);
	}
}
