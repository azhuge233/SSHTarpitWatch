using System.Net.Sockets;

namespace SSHTarpitWatch.Tarpit;

/// <summary>对端断开 / 收盘取消 / 超时这一路的“正常”异常：属预期路径，不算故障。</summary>
internal static class SocketFailures
{
	public static bool IsBenign(Exception ex) =>
		ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException;
}
