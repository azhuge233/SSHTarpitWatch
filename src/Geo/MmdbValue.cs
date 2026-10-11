using Microsoft.Win32.SafeHandles;

namespace SSHTarpitWatch.Geo;

/// <summary>MMDB 类型号（MaxMind DB File Format Specification v2.0 的 output data section 类型表）。</summary>
internal enum MmdbType
{
	Pointer = 1,
	Utf8String = 2,
	Double = 3,
	Bytes = 4,
	Uint16 = 5,
	Uint32 = 6,
	Map = 7,
	Int32 = 8,
	Uint64 = 9,
	Uint128 = 10,
	Array = 11,
	Boolean = 14,
	Float = 15,
}

/// <summary>
/// 一个解析出的值：Payload / Size 依类型而定（String/Bytes = 载荷偏移与字节数；Map/Array = 首个元素偏移与元素数；
/// Uint16/Uint32/Uint64 = 数值 Number）。Next = 该值编码之后的偏移，仅标量有效；
/// 容器为 -1——容器（含被指针指向的）后面还有内容时用 <see cref="MmdbDecoder.SkipValue"/> 定位。
/// </summary>
internal readonly record struct MmdbValue(MmdbType Type, long Payload, int Size, ulong Number, long Next);

/// <summary>MMDB 文件的原始随机读与元数据标记定位（读取器与解码器共用）。</summary>
internal static class MmdbFile
{
	/// <summary>元数据标记（规范：\xAB\xCD\xEF + "MaxMind.com"，共 14 字节）。</summary>
	public static readonly byte[] MetadataMarker =
		[0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m'];

	/// <summary>元数据段（含标记）上限 128KiB——标记只会出现在文件最后这么多字节里。</summary>
	public const int MetadataMaxSize = 128 * 1024;

	/// <summary>数据段分隔（搜索树与数据段之间的 16 字节 NULL；数据段开头 = 搜索树字节数 + 本值）。</summary>
	public const int DataSectionSeparatorSize = 16;

	/// <summary>随机读：越界与短读按损坏报错（不返回部分结果）。</summary>
	public static void ReadAt(SafeFileHandle handle, long offset, Span<byte> destination)
	{
		int read = 0;
		while (read < destination.Length)
		{
			int count = RandomAccess.Read(handle, destination[read..], offset + read);
			if (count <= 0)
			{
				throw new InvalidDataException("unexpected end of mmdb file");
			}

			read += count;
		}
	}

	/// <summary>在文件最后 128KiB 内找元数据标记的最后一次出现；返回标记起始偏移（找不到 -1）。</summary>
	public static long FindMetadataMarker(SafeFileHandle handle, long length)
	{
		int windowSize = (int)Math.Min(MetadataMaxSize, length);
		long windowStart = length - windowSize;
		byte[] window = new byte[windowSize];
		ReadAt(handle, windowStart, window);
		for (int i = windowSize - MetadataMarker.Length; i >= 0; i--)
		{
			if (window.AsSpan(i, MetadataMarker.Length).SequenceEqual(MetadataMarker))
			{
				return windowStart + i;
			}
		}

		return -1;
	}
}
