using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// ip2region xdb 读取器（格式常量对照官方绑定 binding/golang/xdb 与 binding/csharp/IP2Region.Net）：
/// 头部 256 字节（小端）→ 向量索引（256 行 × 256 列 × 8 字节，位于偏移 256）→ 段索引（定长记录）→
/// 数据段（UTF-8 行：国家|省|市|ISP|国家码，官方 v4 库实测序）。
/// 只支持向量索引（IndexPolicy=1）；BTree 策略视为不支持（打开失败，调用方跳过该库并记日志）。
/// v4 文件只答 v4、v6 文件只答 v6（地址族不符时跳过该库）；不整库载入内存，按需随机读。
/// </summary>
internal sealed class XdbReader : IGeoDatabaseReader
{
	/// <summary>头部长度 = 向量索引起始偏移（官方绑定的 HeaderInfoLength）。</summary>
	public const int HeaderInfoLength = 256;

	/// <summary>向量索引总字节数：256×256×8 = 512KiB。</summary>
	public const int VectorIndexSize = 256 * 256 * 8;

	private const int structure20 = 2;         // v2 结构：只支持 IPv4
	private const int structure30 = 3;         // v3 结构：带 IPVersion 字段（4 / 6）
	private const int vectorIndexPolicy = 1;
	private const int btreeIndexPolicy = 2;
	private const int headerFieldLength = 20;  // 到 IPVersion / RuntimePtrBytes 为止的头部字段长度
	private const int maxDataLength = 4096;    // 单条数据记录上限（防御坏库的巨大 dataLen）

	private readonly SafeFileHandle handle;
	private readonly long fileLength;
	private readonly string source;
	private readonly int addressBytes;
	private readonly int segmentSize;

	private XdbReader(SafeFileHandle handle, long fileLength, string source, int addressBytes)
	{
		this.handle = handle;
		this.fileLength = fileLength;
		this.source = source;
		this.addressBytes = addressBytes;
		segmentSize = addressBytes * 2 + 6;
	}

	public string Kind => "xdb";

	/// <summary>打开并按 xdb 解析；不是 xdb（或文件不可读 / 策略不支持）返回 null + 原因。</summary>
	public static XdbReader? TryOpen(string path, out string error)
	{
		SafeFileHandle? handle = null;
		try
		{
			handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			XdbReader? reader = TryParse(handle, path, out error);
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
		addressBytes == 4
			? address.AddressFamily == AddressFamily.InterNetwork
			: address.AddressFamily == AddressFamily.InterNetworkV6;

	public GeoResult? Lookup(IPAddress address, string language)
	{
		if (!CanLookup(address))
		{
			return null; // 地址族不符：跳过该库（同列表里可以同时放 v4 与 v6 文件）
		}

		byte[] ipBytes = address.GetAddressBytes();
		byte[] vector = new byte[8];
		ReadAt(handle, HeaderInfoLength + (ipBytes[0] * 256 + ipBytes[1]) * 8, vector, fileLength);
		uint startPtr = BinaryPrimitives.ReadUInt32LittleEndian(vector);
		uint endPtr = BinaryPrimitives.ReadUInt32LittleEndian(vector.AsSpan(4));
		if (startPtr == 0 || endPtr == 0)
		{
			return null; // 零指针 = 该前缀没有数据（官方绑定同口径）
		}

		if (endPtr < startPtr)
		{
			throw new InvalidDataException("xdb segment index range is invalid");
		}

		// 段索引内二分：记录定长，官方绑定 h = (ePtr − sPtr) / 记录长度，区间含两端
		byte[] record = new byte[segmentSize];
		long low = 0;
		long high = ((long)endPtr - startPtr) / segmentSize;
		uint dataLength = 0;
		uint dataPtr = 0;
		while (low <= high)
		{
			long middle = (low + high) >> 1;
			ReadAt(handle, startPtr + middle * segmentSize, record, fileLength);
			int startCompare = Compare(ipBytes, record.AsSpan(0, addressBytes));
			int endCompare = Compare(ipBytes, record.AsSpan(addressBytes, addressBytes));
			if (startCompare < 0)
			{
				high = middle - 1;
			}
			else if (endCompare > 0)
			{
				low = middle + 1;
			}
			else
			{
				dataLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(addressBytes * 2));
				dataPtr = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(addressBytes * 2 + 2));
				break;
			}
		}

		if (dataLength == 0)
		{
			return null; // 段内无匹配（或 dataLen = 0）
		}

		if (dataLength > maxDataLength
			|| dataPtr < HeaderInfoLength + VectorIndexSize
			|| (long)dataPtr + dataLength > fileLength)
		{
			throw new InvalidDataException("xdb data record is out of range");
		}

		byte[] data = new byte[dataLength];
		ReadAt(handle, dataPtr, data, fileLength);
		return ParseRecord(Encoding.UTF8.GetString(data));
	}

	public void Dispose() => handle.Dispose();

	private static XdbReader? TryParse(SafeFileHandle handle, string path, out string error)
	{
		error = "";
		long length = RandomAccess.GetLength(handle);
		if (length < headerFieldLength)
		{
			error = "file is too small to be an xdb";
			return null;
		}

		byte[] header = new byte[headerFieldLength];
		ReadAt(handle, 0, header, length);
		int version = BinaryPrimitives.ReadUInt16LittleEndian(header);
		int policy = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
		int ipVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
		if (version is not (structure20 or structure30))
		{
			error = $"unsupported xdb structure version {version}";
			return null;
		}

		// 官方绑定 VersionFromHeader 同口径：v2 结构只支持 v4；v3 结构看 IPVersion 字段
		int addressBytes = version == structure20
			? 4
			: ipVersion switch
			{
				4 => 4,
				6 => 16,
				_ => 0,
			};
		if (addressBytes == 0)
		{
			error = $"unsupported xdb ip version {ipVersion}";
			return null;
		}

		if (policy == btreeIndexPolicy)
		{
			error = "xdb BTree index policy is not supported (vector index required)";
			return null;
		}

		if (policy != vectorIndexPolicy)
		{
			error = $"unsupported xdb index policy {policy}";
			return null;
		}

		if (length < HeaderInfoLength + VectorIndexSize)
		{
			error = "file is too small to be an xdb (vector index missing)";
			return null;
		}

		Log.Debug($"GEO xdb path={path} version={version} policy={policy} address_bytes={addressBytes} "
			+ $"created_at={BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))} "
			+ $"start_index_ptr={BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))} "
			+ $"end_index_ptr={BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))}");
		return new XdbReader(handle, length, Path.GetFileName(path), addressBytes);
	}

	/// <summary>
	/// 数据记录解析：字段以 | 分隔（国家|省|市|ISP|国家码，官方 v4 库实测序）；值为 "0" 或空 = 该字段无值；
	/// 五个字段全无值 → 无结果。
	/// </summary>
	private GeoResult? ParseRecord(string text)
	{
		string[] fields = text.Trim().Split('|');
		GeoResult result = new(Field(fields, 0), Field(fields, 4), Field(fields, 1), Field(fields, 2), Field(fields, 3), source);
		return result.IsEmpty ? null : result;
	}

	private static string Field(string[] fields, int index)
	{
		if (index >= fields.Length)
		{
			return "";
		}

		string value = fields[index].Trim();
		return value is "" or "0" ? "" : value;
	}

	/// <summary>
	/// 地址比较：v4 的段索引以小端字节序存 IP（官方绑定 IPv4Compare 把存储字节反向比较）；
	/// v6 直接按字节序比较（官方 IPv6Compare = bytes.Compare）。
	/// </summary>
	private int Compare(byte[] target, ReadOnlySpan<byte> stored)
	{
		if (addressBytes == 16)
		{
			return target.AsSpan().SequenceCompareTo(stored);
		}

		for (int i = 0; i < 4; i++)
		{
			int difference = target[i] - stored[3 - i];
			if (difference != 0)
			{
				return difference < 0 ? -1 : 1;
			}
		}

		return 0;
	}

	/// <summary>随机读：越界与短读按损坏报错（不返回部分结果）。</summary>
	private static void ReadAt(SafeFileHandle handle, long offset, Span<byte> destination, long fileLength)
	{
		if (offset < 0 || offset + destination.Length > fileLength)
		{
			throw new InvalidDataException("xdb read outside of the file");
		}

		int read = 0;
		while (read < destination.Length)
		{
			int count = RandomAccess.Read(handle, destination[read..], offset + read);
			if (count <= 0)
			{
				throw new InvalidDataException("unexpected end of xdb file");
			}

			read += count;
		}
	}
}
