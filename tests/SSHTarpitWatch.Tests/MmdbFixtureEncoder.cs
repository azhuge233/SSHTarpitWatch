using System.Text;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// MMDB typed data 编码器（夹具构造用）：控制字节 = [高 3 位类型][低 5 位长度]，类型 &gt; 7 走扩展型
/// （类型字节 = 类型 − 7）；长度 ≥29 追加 1/2/3 字节；字符串可去重写成指针（覆盖指针解码路径）。
/// </summary>
internal static class MmdbFixtureEncoder
{
	/// <summary>把一个值编码为字节（不去重）——解码器级测试用。</summary>
	public static byte[] Encode(object value)
	{
		using MemoryStream stream = new();
		WriteValue(stream, value, share: false, sharedStrings: null);
		return stream.ToArray();
	}

	/// <summary>写一个值；share = true 且给定 sharedStrings 时，重复字符串写成指向上一次的指针。</summary>
	public static void WriteValue(Stream stream, object value, bool share, Dictionary<string, long>? sharedStrings)
	{
		switch (value)
		{
			case string text:
				WriteString(stream, text, share, sharedStrings);
				break;
			case bool flag:
				WriteControl(stream, 14, flag ? 1 : 0);
				break;
			case ushort number:
				WriteControl(stream, 5, 2);
				WriteBigEndian(stream, number, 2);
				break;
			case uint number:
				WriteControl(stream, 6, 4);
				WriteBigEndian(stream, number, 4);
				break;
			case ulong number:
				WriteControl(stream, 9, 8);
				WriteBigEndian(stream, (long)number, 8);
				break;
			case Dictionary<string, object> map:
				WriteControl(stream, 7, map.Count);
				foreach ((string key, object item) in map)
				{
					WriteValue(stream, key, share: false, sharedStrings); // 键不参与去重
					WriteValue(stream, item, share, sharedStrings);
				}

				break;
			case List<object> list:
				WriteControl(stream, 11, list.Count);
				foreach (object item in list)
				{
					WriteValue(stream, item, share, sharedStrings);
				}

				break;
			default:
				throw new InvalidOperationException($"fixture encoder does not support {value.GetType()}");
		}
	}

	private static void WriteString(Stream stream, string text, bool share, Dictionary<string, long>? sharedStrings)
	{
		if (share && sharedStrings is not null && sharedStrings.TryGetValue(text, out long offset))
		{
			stream.WriteByte(0b001_11000); // 指针，SS=3（后 4 字节 u32 目标偏移）
			WriteBigEndian(stream, offset, 4);
			return;
		}

		if (share && sharedStrings is not null)
		{
			sharedStrings[text] = stream.Position;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(text);
		WriteControl(stream, 2, bytes.Length);
		stream.Write(bytes);
	}

	private static void WriteControl(Stream stream, int type, int size)
	{
		int typeBits = type > 7 ? 0 : type;
		int sizeCode = size < 29 ? size : size < 285 ? 29 : size < 65821 ? 30 : 31;
		stream.WriteByte((byte)((typeBits << 5) | sizeCode));
		if (type > 7)
		{
			stream.WriteByte((byte)(type - 7));
		}

		switch (sizeCode)
		{
			case 29:
				stream.WriteByte((byte)(size - 29));
				break;
			case 30:
				WriteBigEndian(stream, size - 285, 2);
				break;
			case 31:
				WriteBigEndian(stream, size - 65821, 3);
				break;
			default:
				break;
		}
	}

	/// <summary>大端写入低 count 字节（节点记录与数值载荷共用）。</summary>
	public static void WriteBigEndian(Stream stream, long value, int count)
	{
		for (int i = count - 1; i >= 0; i--)
		{
			stream.WriteByte((byte)((value >> (8 * i)) & 0xFF));
		}
	}
}
