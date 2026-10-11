using System.Net;

namespace SSHTarpitWatch.Geo;

/// <summary>单个地区库的读取器：MMDB 与 xdb 两种实现的公共面。</summary>
internal interface IGeoDatabaseReader : IDisposable
{
	/// <summary>库类型（日志用）：mmdb / xdb。</summary>
	string Kind { get; }

	/// <summary>该库能否回答这个地址族（v4 库不答 v6；xdb v4 / v6 文件各答各的）。</summary>
	bool CanLookup(IPAddress address);

	/// <summary>
	/// 查询：无记录或地址族不符返回 null；损坏数据抛 <see cref="InvalidDataException"/>——
	/// 异常由调用方就地消化（查库失败绝不影响困住行为与通知发送）。
	/// </summary>
	GeoResult? Lookup(IPAddress address, string language);
}

/// <summary>
/// 单文件门面（设计 §3.6）：按文件内容自动识别格式——MMDB 以搜索树开头，只看文件头判不出来，
/// 所以先扫文件尾找 MMDB 元数据标记，找到即按 MMDB 打开；
/// 否则按 xdb 头部解析；两者都不成立 → 该库不可用（返回 null + 原因，调用方记日志 / 启动告警并跳过）。
/// </summary>
internal static class GeoDatabase
{
	/// <summary>打开一个库文件（自动识别格式）；失败返回 null + 原因说明。</summary>
	public static IGeoDatabaseReader? TryOpen(string path, out string error)
	{
		IGeoDatabaseReader? reader = MmdbReader.TryOpen(path, out string mmdbError);
		if (reader is not null)
		{
			error = "";
			return reader;
		}

		IGeoDatabaseReader? xdb = XdbReader.TryOpen(path, out string xdbError);
		if (xdb is not null)
		{
			error = "";
			return xdb;
		}

		// 文件级错误（不存在 / 不可读）两个读取器报同一条：只报一次，避免日志重复
		error = string.Equals(mmdbError, xdbError, StringComparison.Ordinal)
			? mmdbError
			: $"mmdb: {mmdbError}; xdb: {xdbError}";
		return null;
	}
}
