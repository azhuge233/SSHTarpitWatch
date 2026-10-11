using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// notifications.geoip 配置（设计 §4.1 / §4.2 / R4.5）：三键默认值与反序列化、显式 null 兜底、
/// 未知键（含 geoip 段内）报错、databases 数组不触发递归校验。
/// </summary>
[TestClass]
public sealed class GeoConfigTests
{
	private readonly List<string> tempFiles = [];

	[TestCleanup]
	public void Cleanup()
	{
		foreach (string path in tempFiles)
		{
			File.Delete(path);
		}

		tempFiles.Clear();
	}

	[TestMethod]
	public void Load_GeoipAbsentOrDefaults_AllThreeKeys()
	{
		ConfigLoadResult absent = ConfigLoader.Load(Write("{}"));
		Assert.AreEqual(0, absent.Errors.Count);
		Assert.IsFalse(absent.Config.Notifications.Geoip.Enabled, "enabled 默认 false");
		Assert.AreEqual(0, absent.Config.Notifications.Geoip.Databases.Length, "databases 默认空数组");
		Assert.AreEqual("zh", absent.Config.Notifications.Geoip.Language, "language 默认 zh");
	}

	[TestMethod]
	public void Load_GeoipSection_ParsesAllKeys()
	{
		ConfigLoadResult result = ConfigLoader.Load(Write(
			"""{"notifications": {"geoip": {"enabled": true, "databases": ["a.mmdb", "b.xdb"], "language": "en"}}}"""));

		Assert.AreEqual(0, result.Errors.Count);
		Assert.IsTrue(result.Config.Notifications.Geoip.Enabled);
		CollectionAssert.AreEqual(
			new[] { "a.mmdb", "b.xdb" }, result.Config.Notifications.Geoip.Databases, "数组键不参与递归校验");
		Assert.AreEqual("en", result.Config.Notifications.Geoip.Language);
	}

	[TestMethod]
	public void Load_GeoipExplicitNull_FallsBackToDefaults()
	{
		// 可空类型（数组 / 字符串）的显式 null 经 Normalize 兜底；bool 的 null 不是合法 JSON 类型，走解析报错（与其他段同口径）
		ConfigLoadResult result = ConfigLoader.Load(Write(
			"""{"notifications": {"geoip": {"databases": null, "language": null}}}"""));

		Assert.AreEqual(0, result.Errors.Count, "显式 null 经 Normalize 兜底");
		Assert.IsFalse(result.Config.Notifications.Geoip.Enabled);
		Assert.AreEqual(0, result.Config.Notifications.Geoip.Databases.Length);
		Assert.AreEqual("zh", result.Config.Notifications.Geoip.Language);
	}

	[TestMethod]
	public void Load_GeoipLanguage_Trimmed()
	{
		// 语言码顺手去空白：" zh " 这类笔误不该因空格静默滑向英文回退
		ConfigLoadResult result = ConfigLoader.Load(Write(
			"""{"notifications": {"geoip": {"language": " zh "}}}"""));

		Assert.AreEqual(0, result.Errors.Count);
		Assert.AreEqual("zh", result.Config.Notifications.Geoip.Language);
	}

	[TestMethod]
	[DataRow("""{"notifications": {"geoip": {"enable": true}}}""", "notifications.geoip.enable")]
	[DataRow("""{"notifications": {"geoip": {"databases": ["a"]}, "geoips": {}}}""", "notifications.geoips")]
	public void Load_GeoipUnknownKeys_Reported(string json, string badKey)
	{
		ConfigLoadResult result = ConfigLoader.Load(Write(json));

		Assert.AreEqual(1, result.Errors.Count, string.Join(" | ", result.Errors));
		StringAssert.Contains(result.Errors[0], badKey);
	}

	[TestMethod]
	public void Validate_GeoipEmptyDatabasePath_Reported()
	{
		AppConfig config = new();
		config.Notifications.Geoip.Databases = ["city.mmdb", "  "];

		List<string> errors = config.Validate();

		Assert.AreEqual(1, errors.Count, "空路径是配置笔误——报错优于静默跳过");
		StringAssert.Contains(errors[0], "notifications.geoip.databases");
	}

	private string Write(string json)
	{
		string path = Path.Combine(Path.GetTempPath(), $"sshtw-geo-cfg-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, json);
		tempFiles.Add(path);
		return path;
	}
}
