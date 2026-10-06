namespace SSHTarpitWatch.Config;

/// <summary>
/// 当前配置快照容器：SIGHUP 重载时整体替换（设计 §4.3）。
/// 读取方拿到的一定是某个完整快照——存量连接在每次发送/判定时读取当前快照，即刻采用新值。
/// </summary>
internal sealed class ConfigStore
{
	private AppConfig current;

	public ConfigStore(AppConfig initial) => current = initial;

	public AppConfig Current => Volatile.Read(ref current);

	public void Replace(AppConfig nextConfig) => Volatile.Write(ref current, nextConfig);
}
