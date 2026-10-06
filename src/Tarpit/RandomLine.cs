namespace SSHTarpitWatch.Tarpit;

/// <summary>随机行生成——对齐 endlessh randline()（设计 §2.1）。</summary>
internal static class RandomLine
{
	private const byte minPrintable = 32;
	private const byte maxPrintable = 126;

	/// <summary>
	/// 生成一行：内容 = 3..lineLength 个随机可打印 ASCII（32..126），其后追加 CRLF。
	/// 内容以 "SSH-" 开头时把首字符改成 'X'——保证永不发出合法 SSH 身份串（R1.3）。
	/// </summary>
	public static byte[] Generate(int lineLength) => Generate(lineLength, Random.Shared);

	/// <summary>测试缝（设计 §3.5）：注入确定性随机源以覆盖 `SSH-` 改写分支；生产恒用 <see cref="Random.Shared"/>。</summary>
	public static byte[] Generate(int lineLength, Random random)
	{
		int contentLength = 3 + random.Next(lineLength - 2);
		byte[] line = new byte[contentLength + 2];
		for (int i = 0; i < contentLength; i++)
		{
			line[i] = (byte)random.Next(minPrintable, maxPrintable + 1);
		}

		if (contentLength >= 4
			&& line[0] == (byte)'S' && line[1] == (byte)'S' && line[2] == (byte)'H' && line[3] == (byte)'-')
		{
			line[0] = (byte)'X';
		}

		line[contentLength] = (byte)'\r';
		line[contentLength + 1] = (byte)'\n';
		return line;
	}
}
