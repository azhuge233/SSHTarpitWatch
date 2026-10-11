using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 合成 ip2region xdb 夹具构造器（自造；格式常量对照官方绑定 binding/golang/xdb 与 binding/csharp）：
/// [256 字节头部][512KiB 向量索引][段索引（按地址前两字节分组、组内按起始地址排序且连续）][数据段]。
/// v4 的段索引以小端字节序存 IP（官方 IPv4Compare 反向比较）；v6 按原字节序存。
/// </summary>
internal sealed class XdbFixture
{
	/// <summary>头部长度 = 向量索引起始偏移（HeaderInfoLength）。</summary>
	public const int HeaderInfoLength = 256;

	/// <summary>向量索引总字节数：256×256×8 = 512KiB。</summary>
	public const int VectorIndexSize = 256 * 256 * 8;

	/// <summary>结构版本：2 = 只支持 v4；3 = 看 <see cref="IpVersion"/>。</summary>
	public int StructureVersion { get; set; } = 2;

	/// <summary>索引策略：1 = 向量索引（支持）；2 = BTree（不支持，拒绝用例）。</summary>
	public int IndexPolicy { get; set; } = 1;

	/// <summary>地址族：4 / 6（v3 结构写入头部；v2 结构只支持 v4）。</summary>
	public int IpVersion { get; set; } = 4;

	private readonly List<(byte[] Start, byte[] End, byte[] Data)> segments = [];

	/// <summary>加入一条段（start / end 为闭区间两端的地址；data 形如 国家|省|市|ISP|国家码）。</summary>
	public void Add(string start, string end, string data)
	{
		byte[] startBytes = IPAddress.Parse(start).GetAddressBytes();
		byte[] endBytes = IPAddress.Parse(end).GetAddressBytes();
		int expected = IpVersion == 6 ? 16 : 4;
		if (startBytes.Length != expected || endBytes.Length != expected)
		{
			throw new InvalidOperationException("segment address does not match ip version");
		}

		segments.Add((startBytes, endBytes, Encoding.UTF8.GetBytes(data)));
	}

	/// <summary>把夹具序列化成 xdb 文件字节。</summary>
	public byte[] ToBytes()
	{
		int addressBytes = IpVersion == 6 ? 16 : 4;
		int segmentSize = addressBytes * 2 + 6;
		long segmentIndexStart = HeaderInfoLength + VectorIndexSize;

		// 分组（键 = 地址前两字节，与查询侧的向量索引下标一致）并在组内按起始地址排序
		List<(byte[] Start, byte[] End, byte[] Data, int Key)> ordered = [];
		foreach (IGrouping<int, (byte[] Start, byte[] End, byte[] Data)> group in segments
			.GroupBy(segment => (segment.Start[0] << 8) | segment.Start[1])
			.OrderBy(group => group.Key))
		{
			foreach ((byte[] start, byte[] end, byte[] data) in group.OrderBy(
				segment => Convert.ToHexString(segment.Start), StringComparer.Ordinal))
			{
				ordered.Add((start, end, data, group.Key));
			}
		}

		// 布局：段索引连续排布；数据段紧随其后，逐条顺序分配
		long dataOffset = segmentIndexStart + (long)ordered.Count * segmentSize;
		long recordOffset = segmentIndexStart;
		long dataCursor = dataOffset;
		List<(byte[] Start, byte[] End, byte[] Data, long RecordOffset, long DataOffset)> placed = [];
		foreach ((byte[] start, byte[] end, byte[] data, _) in ordered)
		{
			placed.Add((start, end, data, recordOffset, dataCursor));
			recordOffset += segmentSize;
			dataCursor += data.Length;
		}

		// 向量索引：(sPtr, ePtr) = 该前缀组首 / 末记录的文件偏移
		byte[] vector = new byte[VectorIndexSize];
		foreach (IGrouping<int, (byte[] Start, byte[] End, byte[] Data, long RecordOffset, long DataOffset)> group in
			placed.GroupBy(item => (item.Start[0] << 8) | item.Start[1]))
		{
			int index = group.Key * 8;
			BinaryPrimitives.WriteUInt32LittleEndian(vector.AsSpan(index), (uint)group.First().RecordOffset);
			BinaryPrimitives.WriteUInt32LittleEndian(vector.AsSpan(index + 4), (uint)group.Last().RecordOffset);
		}

		using MemoryStream file = new();
		WriteUInt16(file, StructureVersion);
		WriteUInt16(file, IndexPolicy);
		WriteUInt32(file, 1_700_000_000);            // created_at
		WriteUInt32(file, (uint)segmentIndexStart);  // start_index_ptr（官方绑定不用于搜索）
		WriteUInt32(file, (uint)(dataOffset - 1));   // end_index_ptr
		WriteUInt16(file, IpVersion);                // v3 结构的 IPVersion 字段
		WriteUInt16(file, 0);                        // runtime_ptr_bytes
		file.Write(new byte[HeaderInfoLength - 20]);
		file.Write(vector);
		foreach ((byte[] start, byte[] end, byte[] data, _, long recordDataOffset) in placed)
		{
			file.Write(Stored(start, addressBytes));
			file.Write(Stored(end, addressBytes));
			WriteUInt16(file, data.Length);
			WriteUInt32(file, (uint)recordDataOffset);
		}

		foreach ((_, _, byte[] data, _, _) in placed)
		{
			file.Write(data);
		}

		return file.ToArray();
	}

	/// <summary>段索引里的 IP 存储形式：v4 = 小端（字节反序，官方 IPv4Compare 反向比较）；v6 = 原序。</summary>
	private static byte[] Stored(byte[] address, int addressBytes)
	{
		byte[] stored = (byte[])address.Clone();
		if (addressBytes == 4)
		{
			Array.Reverse(stored);
		}

		return stored;
	}

	private static void WriteUInt16(Stream stream, int value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
		stream.Write(buffer);
	}

	private static void WriteUInt32(Stream stream, long value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value);
		stream.Write(buffer);
	}
}
