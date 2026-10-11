namespace SSHTarpitWatch.Geo;

/// <summary>
/// 统一地区结果（设计 §3.6）：MMDB 与 xdb 两种库归一后的字段集合；空串 = 该库给不出该字段。
/// 多库合并时逐字段取第一个非空值（库顺序 = 配置顺序 = 优先级）；Source 为贡献字段的库文件名（"+" 连接，诊断用）。
/// </summary>
internal sealed record GeoResult(string Country, string CountryCode, string Region, string City, string Isp, string Source)
{
	/// <summary>五个取值字段是否全空——全空视为「无查询结果」（设计 §3.6）。</summary>
	public bool IsEmpty => Country.Length == 0 && CountryCode.Length == 0 && Region.Length == 0
		&& City.Length == 0 && Isp.Length == 0;

	/// <summary>是否有任何地区字段（国家 / 国家码 / 省 / 市）——只有 ISP/ASN 的库不渲染地区行（测试用于断言该情形；渲染侧按同口径自行判断）。</summary>
	public bool HasRegion => Country.Length > 0 || CountryCode.Length > 0 || Region.Length > 0 || City.Length > 0;
}

/// <summary>
/// 地区查询状态（设计 §3.6 的降级四态）：NoDatabase = 0 是默认值——手工构造的事件与未接线路径
/// 不渲染任何地区内容（与启用前的行为一致）。
/// </summary>
internal enum GeoStatus
{
	/// <summary>enabled=false：不查询、通知整行不出现。</summary>
	NoDatabase = 0,

	/// <summary>enabled=true 但没有任何可用库（未配库 / 文件不可读 / 格式不识）：通知显示「库缺失」。</summary>
	DbMissing,

	/// <summary>库可用但该地址无记录：通知显示「无查询结果」。</summary>
	NoRecord,

	/// <summary>查到结果（Result 非空）。</summary>
	Ok,
}

/// <summary>查询结果 + 状态：随连接期快照从连接时带到断开时（设计 §3.1）。</summary>
internal readonly record struct GeoLookupResult(GeoResult? Result, GeoStatus Status)
{
	/// <summary>未启用（默认值：不渲染地区内容）。</summary>
	public static GeoLookupResult Disabled { get; } = new(null, GeoStatus.NoDatabase);

	/// <summary>没有可用库。</summary>
	public static GeoLookupResult Missing { get; } = new(null, GeoStatus.DbMissing);

	/// <summary>无记录。</summary>
	public static GeoLookupResult NoRecord { get; } = new(null, GeoStatus.NoRecord);

	/// <summary>查到结果。</summary>
	public static GeoLookupResult Found(GeoResult result) => new(result, GeoStatus.Ok);
}
