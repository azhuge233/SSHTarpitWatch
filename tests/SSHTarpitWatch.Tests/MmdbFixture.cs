using System.Globalization;
using System.Net;
using System.Text;

namespace SSHTarpitWatch.Tests;

/// <summary>
/// 合成 MMDB 夹具构造器（自造，不引第三方测试数据——避开额外许可）：按 MaxMind DB File Format Specification v2.0
/// 写出合法文件 = [搜索树][16 字节 NULL 分隔][数据段][元数据标记 + 元数据]。
/// 支持 24/28/32 位记录、v4/v6 树、重复字符串写成指针（覆盖指针解码路径）、元数据键注入（损坏用例）。
/// </summary>
internal sealed class MmdbFixture
{
	/// <summary>元数据标记（\xAB\xCD\xEF + "MaxMind.com"）。</summary>
	private static readonly byte[] marker =
		[0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m'];

	private readonly List<(byte[] Network, int PrefixLength, Dictionary<string, object> Record)> entries = [];

	/// <summary>记录位宽：24 / 28 / 32。</summary>
	public int RecordSize { get; set; } = 24;

	/// <summary>树的地址族：4 / 6。</summary>
	public int IpVersion { get; set; } = 4;

	public string DatabaseType { get; set; } = "Test-City";

	public string[] Languages { get; set; } = ["zh", "en"];

	/// <summary>重复字符串写成指针（覆盖指针解码路径）。</summary>
	public bool ShareStrings { get; set; }

	/// <summary>元数据篡改钩子（损坏用例：删必需键 / 改成非法取值）。</summary>
	public Action<Dictionary<string, object>>? MutateMetadata { get; set; }

	/// <summary>构造一条城市级记录（缺省参数不写入对应段——稀疏记录用例）。</summary>
	public static Dictionary<string, object> Record(
		string code = "",
		Dictionary<string, string>? countryNames = null,
		Dictionary<string, string>? regionNames = null,
		Dictionary<string, string>? cityNames = null,
		uint asn = 0,
		string organization = "")
	{
		Dictionary<string, object> record = [];
		Dictionary<string, object> country = [];
		if (code.Length > 0)
		{
			country["iso_code"] = code;
		}

		if (countryNames is { Count: > 0 })
		{
			country["names"] = Names(countryNames);
		}

		if (country.Count > 0)
		{
			record["country"] = country;
		}

		if (regionNames is { Count: > 0 })
		{
			record["subdivisions"] = new List<object>
			{
				new Dictionary<string, object> { ["names"] = Names(regionNames) },
			};
		}

		if (cityNames is { Count: > 0 })
		{
			record["city"] = new Dictionary<string, object> { ["names"] = Names(cityNames) };
		}

		if (asn > 0 || organization.Length > 0)
		{
			Dictionary<string, object> traits = [];
			if (asn > 0)
			{
				traits["autonomous_system_number"] = asn;
			}

			if (organization.Length > 0)
			{
				traits["autonomous_system_organization"] = organization;
			}

			record["traits"] = traits;
		}

		return record;
	}

	/// <summary>加入一条网络 → 记录（网络形如 "1.2.3.0/24"；不重叠——夹具构造约束）。</summary>
	public void Add(string network, Dictionary<string, object> record)
	{
		int slash = network.IndexOf('/');
		IPAddress address = IPAddress.Parse(network[..slash]);
		int prefixLength = int.Parse(network[(slash + 1)..], CultureInfo.InvariantCulture);
		byte[] bytes = address.GetAddressBytes();
		int expected = IpVersion == 6 ? 16 : 4;
		if (bytes.Length != expected)
		{
			throw new InvalidOperationException($"network {network} does not match ip_version={IpVersion}");
		}

		if (prefixLength < 1 || prefixLength > bytes.Length * 8)
		{
			throw new InvalidOperationException($"bad prefix length in {network}");
		}

		entries.Add((bytes, prefixLength, record));
	}

	/// <summary>把单个值按 MMDB typed data 编码（不参与去重）；解码器级测试用。</summary>
	public static byte[] Encode(object value) => MmdbFixtureEncoder.Encode(value);

	/// <summary>把夹具序列化成 MMDB 文件字节。</summary>
	public byte[] ToBytes()
	{
		Node root = new();
		BuildTree(root);

		List<Node> nodes = [];
		Number(root, nodes);
		int nodeCount = nodes.Count;

		// 数据段：先编码全部记录，得到各记录在数据段内的偏移（记录值 = 节点数 + 16 + 偏移）
		using MemoryStream data = new();
		Dictionary<string, long>? sharedStrings = ShareStrings ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
		long[] recordOffsets = new long[entries.Count];
		for (int i = 0; i < entries.Count; i++)
		{
			recordOffsets[i] = data.Position;
			MmdbFixtureEncoder.WriteValue(data, entries[i].Record, ShareStrings, sharedStrings);
		}

		using MemoryStream file = new();
		foreach (Node node in nodes)
		{
			WriteNode(file, node, RecordSize, nodeCount, recordOffsets);
		}

		file.Write(new byte[16]); // 数据段分隔
		data.Position = 0;
		data.CopyTo(file);
		file.Write(marker);
		MmdbFixtureEncoder.WriteValue(file, Metadata(nodeCount), share: false, sharedStrings: null); // 元数据不参与去重（指针基准不同）
		return file.ToArray();
	}

	private void BuildTree(Node root)
	{
		for (int index = 0; index < entries.Count; index++)
		{
			(byte[] network, int prefixLength, _) = entries[index];
			Node current = root;
			for (int bit = 0; bit < prefixLength; bit++)
			{
				int side = (network[bit >> 3] >> (7 - (bit & 7))) & 1;
				Node? child = side == 0 ? current.LeftChild : current.RightChild;
				int data = side == 0 ? current.LeftData : current.RightData;
				if (bit == prefixLength - 1)
				{
					if (child is not null || data >= 0)
					{
						throw new InvalidOperationException("fixture networks overlap");
					}

					if (side == 0)
					{
						current.LeftData = index;
					}
					else
					{
						current.RightData = index;
					}
				}
				else
				{
					if (data >= 0)
					{
						throw new InvalidOperationException("fixture networks overlap");
					}

					if (child is null)
					{
						child = new Node();
						if (side == 0)
						{
							current.LeftChild = child;
						}
						else
						{
							current.RightChild = child;
						}
					}

					current = child;
				}
			}
		}
	}

	/// <summary>前序遍历编号：节点 0 = 根，节点号即搜索树里的指针值。</summary>
	private static void Number(Node node, List<Node> nodes)
	{
		node.Index = nodes.Count;
		nodes.Add(node);
		if (node.LeftChild is Node left)
		{
			Number(left, nodes);
		}

		if (node.RightChild is Node right)
		{
			Number(right, nodes);
		}
	}

	private static void WriteNode(Stream stream, Node node, int recordSize, int nodeCount, long[] recordOffsets)
	{
		long left = RecordValue(node.LeftChild, node.LeftData, nodeCount, recordOffsets);
		long right = RecordValue(node.RightChild, node.RightData, nodeCount, recordOffsets);
		switch (recordSize)
		{
			case 24:
				MmdbFixtureEncoder.WriteBigEndian(stream, left, 3);
				MmdbFixtureEncoder.WriteBigEndian(stream, right, 3);
				break;
			case 28:
				// 左低 24 位 + 中字节（左高 4 位 | 右高 4 位）+ 右低 24 位
				MmdbFixtureEncoder.WriteBigEndian(stream, left & 0xFFFFFF, 3);
				stream.WriteByte((byte)(((left >> 24) & 0x0F) << 4 | ((right >> 24) & 0x0F)));
				MmdbFixtureEncoder.WriteBigEndian(stream, right & 0xFFFFFF, 3);
				break;
			default:
				MmdbFixtureEncoder.WriteBigEndian(stream, left, 4);
				MmdbFixtureEncoder.WriteBigEndian(stream, right, 4);
				break;
		}
	}

	private static long RecordValue(Node? child, int data, int nodeCount, long[] recordOffsets) =>
		data >= 0 ? nodeCount + 16 + recordOffsets[data] : child?.Index ?? nodeCount;

	private Dictionary<string, object> Metadata(int nodeCount)
	{
		Dictionary<string, object> metadata = new()
		{
			["node_count"] = (uint)nodeCount,
			["record_size"] = (ushort)RecordSize,
			["ip_version"] = (ushort)IpVersion,
			["database_type"] = DatabaseType,
			["languages"] = Languages.Select(language => (object)language).ToList(),
			["build_epoch"] = (ulong)1_700_000_000,
			["description"] = new Dictionary<string, object> { ["en"] = "synthetic test database" },
		};
		MutateMetadata?.Invoke(metadata);
		return metadata;
	}

	private static Dictionary<string, object> Names(Dictionary<string, string> names)
	{
		Dictionary<string, object> map = [];
		foreach ((string language, string name) in names)
		{
			map[language] = name;
		}

		return map;
	}

	/// <summary>搜索树节点：子节点 / 叶子数据记录 / 空记录（三态各自独立占位）。</summary>
	private sealed class Node
	{
		public Node? LeftChild { get; set; }

		public Node? RightChild { get; set; }

		public int LeftData { get; set; } = -1;

		public int RightData { get; set; } = -1;

		public int Index { get; set; }
	}
}
