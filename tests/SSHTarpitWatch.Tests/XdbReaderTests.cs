using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// ip2region xdb 读取器（设计 §3.6 / §7）：合成夹具（自造）——五字段记录、`0` / 空占位、
/// 段边界与多前缀桶二分、v4/v6 地址族、BTree 策略拒绝、坏版本 / 坏文件。
/// </summary>
[TestClass]
public sealed class XdbReaderTests
{
	private TempDirectory? directory;
	private string path = "";

	[TestInitialize]
	public void Setup()
	{
		directory = new TempDirectory();
		path = directory.FilePath("test.xdb");
	}

	[TestCleanup]
	public void Cleanup() => directory?.Dispose();

	[TestMethod]
	public void Xdb_V4Lookup_FiveFieldRecord()
	{
		XdbFixture fixture = new();
		fixture.Add("1.2.3.0", "1.2.3.255", "中国|广东省|深圳市|电信|CN");
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? result = reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh");

		Assert.IsNotNull(result);
		Assert.AreEqual("中国", result.Country);
		Assert.AreEqual("CN", result.CountryCode, "国家码 = 第 5 字段（官方 v4 库实测字段序）");
		Assert.AreEqual("广东省", result.Region, "第 2 字段 = 省");
		Assert.AreEqual("深圳市", result.City);
		Assert.AreEqual("电信", result.Isp);
		Assert.AreEqual("test.xdb", result.Source);
		Assert.AreEqual("xdb", reader.Kind);
	}

	[TestMethod]
	public void Xdb_SegmentBoundaries_AndMultipleBuckets()
	{
		XdbFixture fixture = new();
		fixture.Add("1.2.3.0", "1.2.3.127", "A|省一|市一|ISP1|AA");
		fixture.Add("1.2.3.128", "1.2.3.255", "B|省二|市二|ISP2|BB");
		fixture.Add("9.9.0.0", "9.9.255.255", "C|省三|市三|ISP3|CC");
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? first = reader.Lookup(IPAddress.Parse("1.2.3.1"), "zh");
		Assert.AreEqual("A", first?.Country, "段起点");
		Assert.AreEqual("省一", first?.Region);
		Assert.AreEqual("市一", first?.City);
		Assert.AreEqual("ISP1", first?.Isp, "第 4 字段 = ISP");
		Assert.AreEqual("AA", first?.CountryCode, "第 5 字段 = 国家码");
		Assert.AreEqual("A", reader.Lookup(IPAddress.Parse("1.2.3.127"), "zh")?.Country, "段终点");
		Assert.AreEqual("B", reader.Lookup(IPAddress.Parse("1.2.3.128"), "zh")?.Country, "第二段起点");
		Assert.AreEqual("B", reader.Lookup(IPAddress.Parse("1.2.3.255"), "zh")?.Country, "第二段终点");
		Assert.AreEqual("C", reader.Lookup(IPAddress.Parse("9.9.9.9"), "zh")?.Country, "另一前缀桶");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("1.2.4.0"), "zh"), "桶内无匹配段");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("8.8.8.8"), "zh"), "空桶（向量项全零）");
	}

	[TestMethod]
	public void Xdb_ZeroPlaceholders_AndEmptyRecord()
	{
		XdbFixture fixture = new();
		fixture.Add("1.0.0.0", "1.0.0.255", "美国|0|0|0|0");   // 只给国家
		fixture.Add("2.0.0.0", "2.0.0.255", "0|0|0|0|0");      // 全占位 → 无结果
		fixture.Add("3.0.0.0", "3.0.0.255", "中国||深圳市||0"); // 空字段同样视为无值
		using IGeoDatabaseReader reader = Open(fixture);

		GeoResult? countryOnly = reader.Lookup(IPAddress.Parse("1.0.0.5"), "zh");
		Assert.AreEqual("美国", countryOnly?.Country);
		Assert.AreEqual("", countryOnly?.Region);
		Assert.AreEqual("", countryOnly?.Isp);
		Assert.IsTrue(countryOnly?.HasRegion);

		Assert.IsNull(reader.Lookup(IPAddress.Parse("2.0.0.5"), "zh"), "五字段全占位 → 无结果");

		GeoResult? partial = reader.Lookup(IPAddress.Parse("3.0.0.5"), "zh");
		Assert.AreEqual("中国", partial?.Country);
		Assert.AreEqual("", partial?.Region, "空字段视为无值");
		Assert.AreEqual("深圳市", partial?.City);
		Assert.AreEqual("", partial?.Isp);
	}

	[TestMethod]
	public void Xdb_V6File_AnswersV6Only()
	{
		XdbFixture fixture = new() { StructureVersion = 3, IpVersion = 6 };
		fixture.Add("2001:db8::", "2001:db8::ffff", "中国|广东省|深圳市|电信|CN");
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.AreEqual("深圳市", reader.Lookup(IPAddress.Parse("2001:db8::1"), "zh")?.City);
		Assert.IsFalse(reader.CanLookup(IPAddress.Parse("1.2.3.4")), "v6 文件不答 v4");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("1.2.3.4"), "zh"));
	}

	[TestMethod]
	public void Xdb_V4File_SkipsV6Address()
	{
		XdbFixture fixture = new();
		fixture.Add("1.2.3.0", "1.2.3.255", "中国|广东省|深圳市|电信|CN");
		using IGeoDatabaseReader reader = Open(fixture);

		Assert.IsFalse(reader.CanLookup(IPAddress.Parse("2001:db8::1")), "v4 文件不答 v6");
		Assert.IsNull(reader.Lookup(IPAddress.Parse("2001:db8::1"), "zh"));
	}

	[TestMethod]
	public void Xdb_BTreePolicyAndBadHeader_OpenFails()
	{
		XdbFixture btree = new() { IndexPolicy = 2 };
		btree.Add("1.2.3.0", "1.2.3.255", "中国|0|0|0|0");
		Assert.IsNull(TryOpen(btree, out string btreeError), "BTree 索引策略本期不支持，应跳过该库");
		StringAssert.Contains(btreeError, "BTree");

		XdbFixture badVersion = new() { StructureVersion = 9 };
		badVersion.Add("1.2.3.0", "1.2.3.255", "中国|0|0|0|0");
		Assert.IsNull(TryOpen(badVersion, out string versionError));
		StringAssert.Contains(versionError, "version");

		XdbFixture badIpVersion = new() { StructureVersion = 3, IpVersion = 5 };
		badIpVersion.Add("1.2.3.0", "1.2.3.255", "中国|0|0|0|0");
		Assert.IsNull(TryOpen(badIpVersion, out string ipVersionError));
		StringAssert.Contains(ipVersionError, "ip version");

		File.WriteAllBytes(path, new byte[1024]); // 太小，读不到向量索引
		Assert.IsNull(GeoDatabase.TryOpen(path, out _), "过小的文件不应被识别为 xdb");
	}

	/// <summary>写夹具并打开（要求成功）。</summary>
	private IGeoDatabaseReader Open(XdbFixture fixture)
	{
		File.WriteAllBytes(path, fixture.ToBytes());
		IGeoDatabaseReader? reader = GeoDatabase.TryOpen(path, out string error);
		Assert.IsNotNull(reader, $"夹具应能打开：{error}");
		return reader;
	}

	/// <summary>写夹具并尝试打开（拒绝用例用）。</summary>
	private IGeoDatabaseReader? TryOpen(XdbFixture fixture, out string error)
	{
		File.WriteAllBytes(path, fixture.ToBytes());
		return GeoDatabase.TryOpen(path, out error);
	}
}
