using System.Net.Sockets;
using System.Text;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Tarpit;

/// <summary>
/// 客户端首行读取（设计 §2.4）：最多 512 字节、遇换行提前结束、5 秒超时、仅保留可打印字符（32..126）。
/// 与慢滴计时并发执行，不阻塞发送；未读到完整一行（超时 / 断开 / 收盘取消）时返回空串——通知里显示 —。
/// </summary>
internal static class BannerReader
{
	private const int byteLimit = 512;
	private const int timeoutMs = 5000;
	private const byte minPrintable = 32;
	private const byte maxPrintable = 126;

	/// <summary>读客户端身份串（如 SSH-2.0-test）；无论何种结束方式都会写一条 debug 的 BANNER 行。</summary>
	public static async Task<string> ReadAsync(Socket socket, string host, CancellationToken token)
	{
		StringBuilder collected = new();
		byte[] buffer = new byte[byteLimit];
		int total = 0;
		bool terminated = false;
		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeoutCts.CancelAfter(timeoutMs);
		try
		{
			while (!terminated && total < buffer.Length)
			{
				int received = await socket.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None, timeoutCts.Token)
					.ConfigureAwait(false);
				if (received <= 0)
				{
					break;
				}

				for (int i = total; i < total + received; i++)
				{
					byte value = buffer[i];
					if (value is (byte)'\n' or (byte)'\r')
					{
						terminated = true;
						break;
					}

					if (value is >= minPrintable and <= maxPrintable)
					{
						collected.Append((char)value);
					}
				}

				total += received;
			}
		}
		catch (Exception ex) when (SocketFailures.IsBenign(ex))
		{
			// 5s 超时 / 对端关闭 / 收盘取消：按未上报处理
		}

		string banner = terminated ? collected.ToString() : "";
		Log.Debug($"BANNER host={host} client=\"{EscapeForLog(banner)}\"");
		return banner;
	}

	/// <summary>日志内的引号与反斜杠转义（内容只含可打印 ASCII，转义最小化）。</summary>
	private static string EscapeForLog(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("\"", "\\\"", StringComparison.Ordinal);
}
