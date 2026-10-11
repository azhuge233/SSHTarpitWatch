using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using SSHTarpitWatch.Geo;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// MMDB typed data 解码器（设计 §3.6 / §7）：指针解引用（相对数据段开头）与资源上限
/// （嵌套深度 / 解码值数）——超限报错返回，不返回部分结果。
/// </summary>
[TestClass]
public sealed class MmdbDecoderTests
{
	private TempDirectory? directory;
	private string path = "";

	[TestInitialize]
	public void Setup()
	{
		directory = new TempDirectory();
		path = directory.FilePath("raw.bin");
	}

	[TestCleanup]
	public void Cleanup() => directory?.Dispose();

	[TestMethod]
	public void Decoder_Pointer_ResolvesToTargetString()
	{
		// 数据段布局：[0] 字符串"中国"；其后 map{"zh": 指针 → 偏移 0}（0xE1 = map 1 对；0x38 = 指针 SS=3 + 4 字节目标）
		byte[] data = [.. MmdbFixture.Encode("中国"), 0xE1, 0x42, (byte)'z', (byte)'h', 0x38, 0, 0, 0, 0];
		int mapOffset = data.Length - 9;
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, pointerBase: 0);

		MmdbValue map = decoder.ReadValue(mapOffset, 0);
		Assert.AreEqual(MmdbType.Map, map.Type);
		Assert.AreEqual(1, map.Size);

		MmdbValue key = decoder.ReadValue(map.Payload, 1, out long afterKey);
		Assert.AreEqual(MmdbType.Utf8String, key.Type);
		Assert.AreEqual("zh", decoder.ReadString(key));

		MmdbValue value = decoder.ReadValue(afterKey, 1, out _);
		Assert.AreEqual(MmdbType.Utf8String, value.Type, "值是指针，应解引用到目标");
		Assert.AreEqual("中国", decoder.ReadString(value));
	}

	[TestMethod]
	public void Decoder_SkipValue_PointerJumpsWithoutFollowing()
	{
		// 跳过指针时只前进指针编码本身（不解引用）：map{"zh": 指针} 的 SkipValue 应回到 map 之后
		byte[] data = [.. MmdbFixture.Encode("中国"), 0xE1, 0x42, (byte)'z', (byte)'h', 0x38, 0, 0, 0, 0];
		int mapOffset = data.Length - 9;
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, pointerBase: 0);

		Assert.AreEqual(data.Length, decoder.SkipValue(mapOffset, 0), "跳过整张 map 应回到其编码末尾");
	}

	[TestMethod]
	public void Decoder_PointerToScalar_AdvancesPastPointerEncoding()
	{
		// 数据段：[0] 字符串"中国"（7 字节）；[7] map{"a": 指针 → 0, "b": uint16 7}
		// 父 map 的推进位置必须是指针编码之后（12），而不是指针目标字符串的结尾（7）——否则逐键错位
		byte[] data = [.. MmdbFixture.Encode("中国"), 0xE2, 0x41, (byte)'a', 0x20, 0x00, 0x41, (byte)'b', 0xA2, 0x00, 0x07];
		int mapOffset = 7;
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, pointerBase: 0);

		MmdbValue map = decoder.ReadValue(mapOffset, 0);
		Assert.AreEqual(MmdbType.Map, map.Type);
		Assert.AreEqual(2, map.Size);

		MmdbValue keyA = decoder.ReadValue(map.Payload, 1, out long afterKeyA);
		Assert.AreEqual("a", decoder.ReadString(keyA));
		MmdbValue valueA = decoder.ReadValue(afterKeyA, 1, out long afterValueA);
		Assert.AreEqual(MmdbType.Utf8String, valueA.Type, "值是指针，应解引用到目标");
		Assert.AreEqual("中国", decoder.ReadString(valueA));
		Assert.AreEqual(12, afterValueA, "指针值的推进位置 = 指针编码之后，不是目标字符串结尾");

		MmdbValue keyB = decoder.ReadValue(afterValueA, 1, out long afterKeyB);
		Assert.AreEqual("b", decoder.ReadString(keyB), "推进正确才能读到第二个键");
		MmdbValue valueB = decoder.ReadValue(afterKeyB, 1, out _);
		Assert.AreEqual(MmdbType.Uint16, valueB.Type);
		Assert.AreEqual(7UL, valueB.Number);
		Assert.AreEqual(data.Length, decoder.SkipValue(mapOffset, 0), "整张 map 跳到其编码末尾");
	}

	[TestMethod]
	public void Decoder_DepthLimit_Rejected()
	{
		// 6 层嵌套 map；maxDepth=4：逐层进入时超限
		object nested = "leaf";
		for (int i = 0; i < 6; i++)
		{
			nested = new Dictionary<string, object> { ["x"] = nested };
		}

		byte[] data = MmdbFixture.Encode(nested);
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, 0, maxDepth: 4);

		InvalidDataException? thrown = null;
		try
		{
			MmdbValue current = decoder.ReadValue(0, 0);
			for (int depth = 1; current.Type == MmdbType.Map; depth++)
			{
				MmdbValue key = decoder.ReadValue(current.Payload, depth, out long next);
				_ = decoder.ReadString(key);
				current = decoder.ReadValue(next, depth, out _);
			}
		}
		catch (InvalidDataException ex)
		{
			thrown = ex;
		}

		Assert.IsNotNull(thrown, "超过深度上限应报错返回，不得给出部分结果");
	}

	[TestMethod]
	public void Decoder_ValueCountLimit_Rejected()
	{
		// 容器先按声明的子元素数扣减预算：10 对键值 vs 预算 8
		Dictionary<string, object> map = [];
		for (int i = 0; i < 10; i++)
		{
			map[$"k{i}"] = "v";
		}

		byte[] data = MmdbFixture.Encode(map);
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, 0, maxValues: 8);

		Assert.ThrowsExactly<InvalidDataException>(() => decoder.ReadValue(0, 0));
	}

	[TestMethod]
	public void Decoder_PayloadBudget_Rejected()
	{
		// 单次物化 string 超预算：字符串 40 字节 vs 预算 16
		byte[] data = MmdbFixture.Encode(new string('x', 40));
		using SafeFileHandle handle = OpenRaw(data);
		var decoder = new MmdbDecoder(handle, data.Length, 0, maxPayloadBytes: 16);

		MmdbValue value = decoder.ReadValue(0, 0);
		Assert.ThrowsExactly<InvalidDataException>(() => decoder.ReadString(value));
	}

	private SafeFileHandle OpenRaw(byte[] content)
	{
		File.WriteAllBytes(path, content);
		return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
	}
}
