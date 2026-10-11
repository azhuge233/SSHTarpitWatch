using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// MMDB typed data 解码器（schema 导向）：只物化提取用到的字段，未知键 / 未知类型的值经
/// <see cref="SkipValue"/> 跳过（不解引用指针、不物化）。
/// 资源上限按规范 Reader Resource Limits：嵌套深度 ≤512（进容器或跟随指针各 +1）、解码值数 ≤65536
/// （容器先按声明的子元素数扣减）、单次物化 string/bytes ≤2MiB；超限抛 <see cref="InvalidDataException"/>。
/// 本类非线程安全（预算按实例计）——每个查询建一个实例。
/// </summary>
internal sealed class MmdbDecoder
{
	/// <summary>最大嵌套深度（规范建议值）。</summary>
	public const int MaxDepth = 512;

	/// <summary>单次解码的最大逻辑值数（规范建议值）。</summary>
	public const int MaxValues = 65536;

	/// <summary>单次物化的 string/bytes 总上限（规范：几 MB 级已很宽裕）。</summary>
	public const int MaxPayloadBytes = 2 * 1024 * 1024;

	private readonly SafeFileHandle handle;
	private readonly long fileLength;
	private readonly long pointerBase;
	private readonly int maxDepth;
	private readonly byte[] scratch = new byte[8];
	private int values;
	private int payload;

	/// <param name="pointerBase">指针基准：数据段开头（搜索树字节数 + 16）或元数据段开头（标记之后）。</param>
	public MmdbDecoder(
		SafeFileHandle handle,
		long fileLength,
		long pointerBase,
		int maxDepth = MaxDepth,
		int maxValues = MaxValues,
		int maxPayloadBytes = MaxPayloadBytes)
	{
		this.handle = handle;
		this.fileLength = fileLength;
		this.pointerBase = pointerBase;
		this.maxDepth = maxDepth;
		values = maxValues - 1; // 根值占一个（规范：root is one occurrence）
		payload = maxPayloadBytes;
	}

	/// <summary>读一个值并返回该值自身编码之后的偏移（父容器据此逐键推进）：一律取 <see cref="SkipValue"/>——
	/// ReadValue 会解引用指针，指针指向标量时其 Next 落在指针目标之后，不能用来推进父容器；
	/// 非指针标量两者一致（值自身结尾），容器本就由 SkipValue 走完。</summary>
	public MmdbValue ReadValue(long offset, int depth, out long next)
	{
		MmdbValue value = ReadValue(offset, depth);
		next = SkipValue(offset, depth);
		return value;
	}

	/// <summary>读一个值并解引用指针（指针指向指针 = 非法，按损坏报错）。</summary>
	public MmdbValue ReadValue(long offset, int depth)
	{
		Control control = ReadControl(offset);
		if (control.Type == MmdbType.Pointer)
		{
			CheckDepth(depth); // 跟随指针增加深度（规范）
			long target = pointerBase + control.Pointer;
			if (target < 0 || target >= fileLength)
			{
				throw new InvalidDataException("mmdb pointer outside of the file");
			}

			control = ReadControl(target);
			if (control.Type == MmdbType.Pointer)
			{
				throw new InvalidDataException("mmdb contains a pointer to another pointer");
			}

			depth++;
		}

		return Inline(control, depth);
	}

	/// <summary>跳过一个值（不解引用指针、不物化），返回其编码之后的偏移（容器递归求结束位置）。</summary>
	public long SkipValue(long offset, int depth)
	{
		Control control = ReadControl(offset);
		switch (control.Type)
		{
			case MmdbType.Pointer:
				return control.Next;

			case MmdbType.Map:
				ChargeContainer(depth, control.Size * 2);
				long mapEnd = control.Payload;
				for (int i = 0; i < control.Size; i++)
				{
					mapEnd = SkipValue(mapEnd, depth + 1);
					mapEnd = SkipValue(mapEnd, depth + 1);
				}

				return mapEnd;

			case MmdbType.Array:
				ChargeContainer(depth, control.Size);
				long arrayEnd = control.Payload;
				for (int i = 0; i < control.Size; i++)
				{
					arrayEnd = SkipValue(arrayEnd, depth + 1);
				}

				return arrayEnd;

			case MmdbType.Boolean:
				return control.Payload; // bool 无载荷：size 即值

			default:
				return control.Next;
		}
	}

	/// <summary>物化一个字符串值（计入物化预算）；非字符串返回空串。</summary>
	public string ReadString(MmdbValue value)
	{
		if (value.Type != MmdbType.Utf8String)
		{
			return "";
		}

		if (value.Size < 0 || value.Size > payload)
		{
			throw new InvalidDataException("mmdb string exceeds the materialization budget");
		}

		payload -= value.Size;
		byte[] bytes = new byte[value.Size];
		MmdbFile.ReadAt(handle, value.Payload, bytes);
		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>标量 / 容器的公共落地：容器先按声明子元素数扣减值预算，数值类型顺带取值。</summary>
	private MmdbValue Inline(Control control, int depth)
	{
		MmdbType type = control.Type;
		if (type is MmdbType.Map or MmdbType.Array)
		{
			ChargeContainer(depth, type == MmdbType.Map ? control.Size * 2 : control.Size);
			return new MmdbValue(type, control.Payload, control.Size, 0, -1);
		}

		ulong number = type switch
		{
			MmdbType.Uint16 or MmdbType.Uint32 or MmdbType.Uint64 => ReadNumber(control),
			_ => 0,
		};
		long next = type == MmdbType.Boolean ? control.Payload : control.Next;
		return new MmdbValue(type, control.Payload, control.Size, number, next);
	}

	private ulong ReadNumber(Control control)
	{
		int maxSize = control.Type switch
		{
			MmdbType.Uint16 => 2,
			MmdbType.Uint32 => 4,
			_ => 8,
		};
		if (control.Size > maxSize)
		{
			throw new InvalidDataException($"mmdb integer of {control.Size} bytes is too large");
		}

		MmdbFile.ReadAt(handle, control.Payload, scratch.AsSpan(0, control.Size));
		ulong value = 0;
		for (int i = 0; i < control.Size; i++)
		{
			value = (value << 8) | scratch[i];
		}

		return value;
	}

	private void ChargeContainer(int depth, int children)
	{
		CheckDepth(depth);
		values -= children;
		if (values < 0)
		{
			throw new InvalidDataException("mmdb decode exceeds the maximum value count");
		}
	}

	private void CheckDepth(int depth)
	{
		if (depth >= maxDepth)
		{
			throw new InvalidDataException("mmdb nesting exceeds the maximum depth");
		}
	}

	/// <summary>
	/// 解析控制字节：顺序 = 控制字节 → [扩展类型字节] → [长度字节]（对齐官方读取器）。
	/// 指针的低 5 位是 SS+VVV（不是长度）：SS=0 → 11 位；1 → 19 位 +2048；2 → 27 位 +526336；3 → 后 4 字节 u32。
	/// </summary>
	private Control ReadControl(long offset)
	{
		byte control = ReadByte(offset);
		offset++;
		MmdbType type = (MmdbType)(control >> 5);
		if (type == 0)
		{
			int extended = ReadByte(offset) + 7;
			offset++;
			if (extended < 8)
			{
				throw new InvalidDataException("mmdb extended type resolves below 8");
			}

			type = (MmdbType)extended;
		}

		int size = control & 0x1F;
		if (type == MmdbType.Pointer)
		{
			int width = ((size >> 3) & 0x3) + 1;
			long pointer = width == 4 ? 0 : size & 0x7;
			for (int i = 0; i < width; i++)
			{
				pointer = (pointer << 8) | ReadByte(offset + i);
			}

			pointer += width switch
			{
				2 => 2048,
				3 => 526336,
				_ => 0,
			};
			return new Control(type, 0, offset, offset + width, pointer);
		}

		if (size >= 29)
		{
			int extra = size - 28;
			long raw = 0;
			for (int i = 0; i < extra; i++)
			{
				raw = (raw << 8) | ReadByte(offset + i);
			}

			size = size switch
			{
				29 => 29 + (int)raw,
				30 => 285 + (int)raw,
				_ => 65821 + (int)raw,
			};
			offset += extra;
		}

		return new Control(type, size, offset, offset + size, 0);
	}

	private byte ReadByte(long offset)
	{
		MmdbFile.ReadAt(handle, offset, scratch.AsSpan(0, 1));
		return scratch[0];
	}

	/// <summary>控制字节的解析结果：Next = 该值编码之后的偏移（容器为内容末尾，需自行走完）。</summary>
	private readonly record struct Control(MmdbType Type, int Size, long Payload, long Next, long Pointer);
}
