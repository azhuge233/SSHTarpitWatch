using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Tarpit;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 随机行生成（设计 §2.1 / §7）：内容长度 ∈ [3, lineLength]（大样本覆盖两端）、
/// 总长 = 内容 + 2 且尾 CRLF、字符集 32..126、永不 `SSH-` 前缀（含 lineLength=3 边界）。
/// 大样本验证“永不出现 `SSH-` 前缀”这一不变式；`SSH-` 改写分支本身经注入随机源确定性覆盖（测试缝，设计 §3.5）。
/// </summary>
[TestClass]
public sealed class RandomLineTests
{
	/// <summary>覆盖下界（3）、刚过改写守卫（4）与上界（255）的多档行长。</summary>
	private static readonly int[] lineLengths = [3, 4, 8, 32, 255];

	[TestMethod]
	public void RandomLine_BoundsCharsetAndCrLf_AcrossLineLengths()
	{
		foreach (int lineLength in lineLengths)
		{
			int minSeen = int.MaxValue;
			int maxSeen = 0;
			for (int i = 0; i < 8000; i++)
			{
				byte[] line = RandomLine.Generate(lineLength);
				int contentLength = line.Length - 2;
				Assert.IsTrue(
					contentLength >= 3 && contentLength <= lineLength,
					$"内容长度越界：{contentLength}（lineLength={lineLength}）");
				Assert.AreEqual((byte)'\r', line[^2], $"行尾应为 CRLF 的 CR（lineLength={lineLength}）");
				Assert.AreEqual((byte)'\n', line[^1], $"行尾应为 CRLF 的 LF（lineLength={lineLength}）");
				for (int b = 0; b < contentLength; b++)
				{
					Assert.IsTrue(
						line[b] is >= 32 and <= 126,
						$"非可打印字节 {line[b]}（lineLength={lineLength}，第 {b} 字节）");
				}

				minSeen = Math.Min(minSeen, contentLength);
				maxSeen = Math.Max(maxSeen, contentLength);
			}

			// 大样本应命中两端：下界恒为 3；上界 = lineLength（lineLength=3 时两端重合）
			Assert.AreEqual(3, minSeen, $"lineLength={lineLength} 大样本应命中下界 3");
			Assert.AreEqual(lineLength, maxSeen, $"lineLength={lineLength} 大样本应命中上界 {lineLength}");
		}
	}

	[TestMethod]
	public void RandomLine_NeverSshPrefix_LargeSample()
	{
		int checkedLines = 0;
		foreach (int lineLength in lineLengths)
		{
			for (int i = 0; i < 5000; i++)
			{
				byte[] line = RandomLine.Generate(lineLength);
				int contentLength = line.Length - 2;
				checkedLines++;

				bool sshPrefix = contentLength >= 4
					&& line[0] == (byte)'S' && line[1] == (byte)'S'
					&& line[2] == (byte)'H' && line[3] == (byte)'-';
				Assert.IsFalse(sshPrefix, $"第 {checkedLines} 行以 SSH- 开头（lineLength={lineLength}）");

				// 改写只允许改首字符：其余内容必须保持可打印、尾部 CRLF 与长度口径不变（改写不破坏其余内容）
				for (int b = 1; b < contentLength; b++)
				{
					Assert.IsTrue(
						line[b] is >= 32 and <= 126,
						$"首字符之后的第 {b} 字节非可打印：{line[b]}（lineLength={lineLength}）");
				}

				Assert.AreEqual((byte)'\r', line[^2], $"尾 CRLF 的 CR 缺失（lineLength={lineLength}）");
				Assert.AreEqual((byte)'\n', line[^1], $"尾 CRLF 的 LF 缺失（lineLength={lineLength}）");
			}
		}

		Assert.IsTrue(checkedLines >= 20000, $"防 SSH- 前缀样本量应 ≥20000，实际 {checkedLines}");
	}

	[TestMethod]
	public void RandomLine_SshPrefixRewrite_Deterministic()
	{
		// 设计 §3.5 测试缝：注入脚本化随机源，构造内容恰以 "SSH-" 开头的行——
		// 断言首字符被改写为 X（R1.3），其余内容与长度口径不变。
		byte[] line = RandomLine.Generate(8, new ScriptedRandom(lengthChoice: 3, 'S', 'S', 'H', '-', 'a', 'b'));

		Assert.AreEqual(8, line.Length, "内容 6 字节 + CRLF");
		Assert.AreEqual((byte)'X', line[0], "SSH- 前缀的首字符应改为 X");
		Assert.AreEqual((byte)'S', line[1], "改写只允许动首字符");
		Assert.AreEqual((byte)'H', line[2]);
		Assert.AreEqual((byte)'-', line[3]);
		Assert.AreEqual((byte)'a', line[4]);
		Assert.AreEqual((byte)'b', line[5]);
		Assert.AreEqual((byte)'\r', line[6]);
		Assert.AreEqual((byte)'\n', line[7]);

		// 边界：内容长度恰为 4（改写守卫的最小长度）时同样生效
		byte[] minLine = RandomLine.Generate(4, new ScriptedRandom(lengthChoice: 1, 'S', 'S', 'H', '-'));
		Assert.AreEqual(6, minLine.Length, "内容 4 字节 + CRLF");
		Assert.AreEqual((byte)'X', minLine[0]);
		Assert.AreEqual((byte)'-', minLine[3]);
	}

	/// <summary>脚本化随机源（测试缝）：Next(max) 与 Next(min,max) 按脚本返回，构造确定性内容。</summary>
	private sealed class ScriptedRandom : Random
	{
		private readonly Queue<int> lengthChoices = new();
		private readonly Queue<int> byteValues = new();

		public ScriptedRandom(int lengthChoice, params char[] content)
		{
			lengthChoices.Enqueue(lengthChoice);
			foreach (char c in content)
			{
				byteValues.Enqueue(c);
			}
		}

		public override int Next(int maxValue) => lengthChoices.Dequeue();

		public override int Next(int minValue, int maxValue) => byteValues.Dequeue();
	}
}
