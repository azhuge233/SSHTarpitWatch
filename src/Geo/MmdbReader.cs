using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// MMDB 读取器（MaxMind DB File Format Specification v2.0）：打开即解析文件末尾的元数据
/// （在最后 128KiB 内找 "\xAB\xCD\xEF MaxMind.com" 标记的最后一次出现），查询时按位遍历二叉搜索树、
/// 按需随机读数据段——不整库载入内存（121MB 的 City 库同样只读用到的字节）。
/// 线程安全（RandomAccess.Read + 每次解码独立预算）；句柄随 Dispose 关闭。
/// </summary>
internal sealed class MmdbReader : IGeoDatabaseReader
{
	private readonly SafeFileHandle handle;
	private readonly long fileLength;
	private readonly string source;
	private readonly int ipVersion;
	private readonly int recordSize;
	private readonly int nodeBytes;
	private readonly long nodeCount;
	private readonly long treeSize;
	private readonly long dataStart;

	private MmdbReader(
		SafeFileHandle handle, long fileLength, string source, int ipVersion, int recordSize, long nodeCount, long treeSize)
	{
		this.handle = handle;
		this.fileLength = fileLength;
		this.source = source;
		this.ipVersion = ipVersion;
		this.recordSize = recordSize;
		nodeBytes = recordSize * 2 / 8;
		this.nodeCount = nodeCount;
		this.treeSize = treeSize;
		dataStart = treeSize + MmdbFile.DataSectionSeparatorSize; // 记录内指针的基准 = 数据段开头
	}

	public string Kind => "mmdb";

	/// <summary>打开并按 MMDB 解析；不是 MMDB（或文件不可读）返回 null + 原因。</summary>
	public static MmdbReader? TryOpen(string path, out string error)
	{
		SafeFileHandle? handle = null;
		try
		{
			handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			MmdbReader? reader = TryParse(handle, path, out error);
			if (reader is null)
			{
				handle.Dispose(); // 解析失败：句柄未移交，由调用方释放
			}

			return reader;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			error = ex.Message;
			handle?.Dispose();
			return null;
		}
	}

	public bool CanLookup(IPAddress address) =>
		ipVersion == 6 || address.AddressFamily == AddressFamily.InterNetwork;

	public GeoResult? Lookup(IPAddress address, string language)
	{
		if (address.AddressFamily == AddressFamily.InterNetwork)
		{
			if (ipVersion == 4)
			{
				return LookupAddress(address.GetAddressBytes(), language);
			}

			// v6 库覆盖 v4：规范把 v4 数据放在高 96 位为 0 的 ::/96；MaxMind 另有 ::ffff:0:0/96 指向 v4 根的别名。
			// 先按规范位置查，再兜底 IPv4-mapped ::ffff:a.b.c.d（只建了别名的库）。
			GeoResult? zeroPadded = LookupAddress(V4InV6(address, mapped: false), language);
			return zeroPadded ?? LookupAddress(V4InV6(address, mapped: true), language);
		}

		return ipVersion == 6 ? LookupAddress(address.GetAddressBytes(), language) : null;
	}

	public void Dispose() => handle.Dispose();

	private static MmdbReader? TryParse(SafeFileHandle handle, string path, out string error)
	{
		error = "";
		long length = RandomAccess.GetLength(handle);
		if (length <= MmdbFile.MetadataMarker.Length)
		{
			error = "file is too small to be an MMDB";
			return null;
		}

		long markerOffset = MmdbFile.FindMetadataMarker(handle, length);
		if (markerOffset < 0)
		{
			error = "MMDB metadata marker not found in the last 128KiB";
			return null;
		}

		long metadataStart = markerOffset + MmdbFile.MetadataMarker.Length;
		var decoder = new MmdbDecoder(handle, length, metadataStart);
		MmdbValue root;
		try
		{
			root = decoder.ReadValue(metadataStart, 0);
		}
		catch (InvalidDataException ex)
		{
			error = $"invalid MMDB metadata: {ex.Message}";
			return null;
		}

		if (root.Type != MmdbType.Map)
		{
			error = "MMDB metadata is not a map";
			return null;
		}

		long nodeCount = 0;
		int recordSize = 0;
		int ipVersion = 0;
		string databaseType = "";
		try
		{
			long position = root.Payload;
			for (int i = 0; i < root.Size; i++)
			{
				MmdbValue key = decoder.ReadValue(position, 1, out long keyNext);
				if (key.Type != MmdbType.Utf8String)
				{
					throw new InvalidDataException("metadata key is not a string");
				}

				string name = decoder.ReadString(key);
				position = keyNext;
				MmdbValue value = decoder.ReadValue(position, 1, out long next);
				switch (name)
				{
					case "node_count":
						nodeCount = (long)value.Number;
						break;
					case "record_size":
						recordSize = (int)value.Number;
						break;
					case "ip_version":
						ipVersion = (int)value.Number;
						break;
					case "database_type":
						databaseType = decoder.ReadString(value);
						break;
					default:
						break; // 其余键（languages / build_epoch / description / binary_format_*）不参与提取
				}

				position = next;
			}
		}
		catch (InvalidDataException ex)
		{
			error = $"invalid MMDB metadata: {ex.Message}";
			return null;
		}

		// 必需键校验（规范：languages / description 可选；我们需要的只有这四个）
		if (nodeCount < 1)
		{
			error = "MMDB metadata: node_count is missing or not positive";
			return null;
		}

		if (recordSize is not (24 or 28 or 32))
		{
			error = $"MMDB metadata: unsupported record_size {recordSize}";
			return null;
		}

		if (ipVersion is not (4 or 6))
		{
			error = $"MMDB metadata: unsupported ip_version {ipVersion}";
			return null;
		}

		if (databaseType.Length == 0)
		{
			error = "MMDB metadata: database_type is missing";
			return null;
		}

		long treeSize = nodeCount * (recordSize * 2 / 8);
		if (treeSize + MmdbFile.DataSectionSeparatorSize > markerOffset)
		{
			error = "MMDB search tree does not fit in the file";
			return null;
		}

		Log.Debug($"GEO mmdb path={path} type={databaseType} ip_version={ipVersion} "
			+ $"record_size={recordSize} node_count={nodeCount}");
		return new MmdbReader(handle, length, Path.GetFileName(path), ipVersion, recordSize, nodeCount, treeSize);
	}

	/// <summary>按位遍历搜索树（0 → 左记录，1 → 右记录）：记录 &lt; 节点数 → 继续；= 节点数 → 无数据；&gt; 节点数 → 数据段。</summary>
	private GeoResult? LookupAddress(byte[] ipBytes, string language)
	{
		long record = 0;
		int bit = 0;
		for (; bit < ipBytes.Length * 8 && record < nodeCount; bit++)
		{
			int side = (ipBytes[bit >> 3] >> (7 - (bit & 7))) & 1;
			record = ReadRecord(record, side);
		}

		if (record == nodeCount)
		{
			return null; // 空记录：该 IP 在库里没有数据
		}

		if (record < nodeCount)
		{
			throw new InvalidDataException("MMDB search tree is corrupt: a record pointed back into the tree");
		}

		// 记录值 − 节点数 + 搜索树字节数 = 数据在文件里的偏移（规范里化简后的公式）
		return MmdbRecordDecoder.Decode(handle, fileLength, dataStart, record - nodeCount + treeSize, language, source);
	}

	/// <summary>读一个节点记录（大端；24 → 6 字节 [3][3]，28 → 7 字节（中字节两侧共享高位），32 → 8 字节 [4][4]）。
	/// 缓冲区为方法局部（栈上）——读取器因此真的可被并发调用（对齐类注释的线程安全承诺）。</summary>
	private long ReadRecord(long node, int side)
	{
		long baseOffset = node * nodeBytes;
		Span<byte> buffer = stackalloc byte[4];
		if (recordSize == 24)
		{
			MmdbFile.ReadAt(handle, baseOffset + side * 3, buffer[..3]);
			return ((long)buffer[0] << 16) | ((long)buffer[1] << 8) | buffer[2];
		}

		if (recordSize == 32)
		{
			MmdbFile.ReadAt(handle, baseOffset + side * 4, buffer);
			return (long)(uint)((buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3]);
		}

		MmdbFile.ReadAt(handle, baseOffset + (side == 0 ? 0 : 3), buffer);
		return side == 0
			? ((long)(buffer[3] & 0xF0) << 20) | ((long)buffer[0] << 16) | ((long)buffer[1] << 8) | buffer[2]
			: ((long)(buffer[0] & 0x0F) << 24) | ((long)buffer[1] << 16) | ((long)buffer[2] << 8) | buffer[3];
	}

	/// <summary>v4 地址 → 16 字节 v6：mapped=true 为 ::ffff:a.b.c.d，否则为高 96 位全 0 的 ::a.b.c.d。</summary>
	private static byte[] V4InV6(IPAddress address, bool mapped)
	{
		byte[] v6 = new byte[16];
		if (mapped)
		{
			v6[10] = 0xFF;
			v6[11] = 0xFF;
		}

		address.GetAddressBytes().CopyTo(v6, 12);
		return v6;
	}
}
