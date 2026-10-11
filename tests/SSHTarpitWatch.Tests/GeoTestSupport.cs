using SSHTarpitWatch.Config;

namespace SSHTarpitWatch.Tests;

/// <summary>测试用临时目录（Dispose 递归清理）——地区库夹具落在这里，不污染仓库与工作树。</summary>
internal sealed class TempDirectory : IDisposable
{
	public TempDirectory()
	{
		Root = Path.Combine(Path.GetTempPath(), $"sshtw-geo-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Root);
	}

	/// <summary>目录绝对路径。</summary>
	public string Root { get; }

	/// <summary>目录内某个文件的完整路径。</summary>
	public string FilePath(string name) => Path.Combine(Root, name);

	/// <summary>写一个文件到目录内并返回其完整路径。</summary>
	public string Write(string name, byte[] content)
	{
		string path = FilePath(name);
		File.WriteAllBytes(path, content);
		return path;
	}

	/// <summary>写一个文本文件到目录内并返回其完整路径。</summary>
	public string WriteText(string name, string content)
	{
		string path = FilePath(name);
		File.WriteAllText(path, content);
		return path;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 清理失败不影响测试结论（Windows 上偶发文件占用）
		}
	}
}

/// <summary>地区解析测试的共用构造（设计 §3.6 / §7：合成夹具 + 配置拼装）。</summary>
internal static class GeoTestSupport
{
	/// <summary>启用 geoip 的通知配置（databases 原样写入——相对路径按调用方给的基准目录解析）。</summary>
	public static AppConfig ConfigWithGeoip(params string[] databases)
	{
		AppConfig config = TestSupport.Notifications(enabled: false, onConnect: false, onClose: true, dedupSeconds: 0);
		config.Notifications.Geoip = new GeoIpConfig { Enabled = true, Databases = databases, Language = "zh" };
		return config;
	}

	/// <summary>拼一条含全部字段的 MMDB 记录（国家 + 省 + 市 + ASN/组织）。</summary>
	public static Dictionary<string, object> FullRecord() => MmdbFixture.Record(
		code: "CN",
		countryNames: new Dictionary<string, string> { ["zh"] = "中国", ["en"] = "China" },
		regionNames: new Dictionary<string, string> { ["zh"] = "广东省", ["en"] = "Guangdong" },
		cityNames: new Dictionary<string, string> { ["zh"] = "深圳市", ["en"] = "Shenzhen" },
		asn: 4134,
		organization: "Chinanet");

	/// <summary>一个网络的 MMDB 夹具字节。</summary>
	public static byte[] MmdbBytes(string network, Dictionary<string, object> record)
	{
		MmdbFixture fixture = new();
		fixture.Add(network, record);
		return fixture.ToBytes();
	}

	/// <summary>一条段的 v4 xdb 夹具字节。</summary>
	public static byte[] XdbBytes(string start, string end, string data)
	{
		XdbFixture fixture = new();
		fixture.Add(start, end, data);
		return fixture.ToBytes();
	}
}
