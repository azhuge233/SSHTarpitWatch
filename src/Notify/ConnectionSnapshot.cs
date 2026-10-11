using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Notify;

/// <summary>
/// 连接期快照（设计 §3.1 连接期快照 / §3.4 内存口径）：连接建立时产生的信息
/// （来源、本地端口、banner、连接时刻、当日 N）连同去重过闸标记随连接保留到断开——
/// 默认的单条断开通知需要它们在断开时仍可用。
/// 地区快照（2026-10-11 定稿）：连接时异步查库得到的地区 / ISP 结果（GeoResult，见 §3.6）一并入快照。
/// 一个在途连接持有一份（上限 = server.max_clients，默认 4096 → 最坏约 2.4MB）；随连接断开（引用释放）回收。
/// </summary>
internal sealed record ConnectionSnapshot(
	string Host,
	int Port,
	int LocalPort,
	string Banner,
	DateTimeOffset ConnectTimeUtc,
	int TodayCount,
	bool GatePassed,
	GeoLookupResult Geo);
