using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace SSHTarpitWatch.Geo;

/// <summary>
/// MMDB 数据记录提取（schema 导向，设计 §3.6）：只认用到的顶层键，其余一律跳过（不递归未知值、不解引用未知指针）。
/// 地区字段：country.iso_code / country.names[language]（MaxMind 系嵌套 map）、subdivisions[0].names[language]、
/// city.names[language]；IPinfo Lite 的扁平形态另取顶层 country_code（国家码）与 country 字符串（国家名）。
/// ISP 覆盖三种形态（组织名按序取首个非空）：traits.autonomous_system_organization（City 系）、
/// 顶层 autonomous_system_organization（GeoLite2-ASN 系）、顶层 as_name（IPinfo Lite，缺 as_name 时退化用 asn 字符串）；
/// 只有 ASN 号时退化为 AS{号}。键名按原样匹配（MMDB 键恒为小写）。记录为空 map（或字段全空）返回 null = 该 IP 无可用结果。
/// </summary>
internal static class MmdbRecordDecoder
{
	public static GeoResult? Decode(
		SafeFileHandle handle, long fileLength, long dataStart, long offset, string language, string source)
	{
		var decoder = new MmdbDecoder(handle, fileLength, dataStart);
		MmdbValue root = decoder.ReadValue(offset, 0, out _);
		if (root.Type != MmdbType.Map)
		{
			throw new InvalidDataException("MMDB data record is not a map");
		}

		string country = "", code = "", region = "", city = "";
		string traitsOrganization = "", topOrganization = "", infoOrganization = "", infoAsn = "";
		ulong traitsNumber = 0, topNumber = 0;
		long position = root.Payload;
		for (int i = 0; i < root.Size; i++)
		{
			string name = ReadKey(decoder, ref position);
			long valueOffset = position;
			switch (name)
			{
				case "country":
					position = ReadCountry(decoder, valueOffset, language, ref code, ref country);
					break;
				case "country_code":
					position = ReadCountryCode(decoder, valueOffset, ref code);
					break;
				case "city":
					position = ReadNamesContainer(decoder, valueOffset, language, ref city);
					break;
				case "subdivisions":
					position = ReadFirstSubdivision(decoder, valueOffset, language, ref region);
					break;
				case "traits":
					position = ReadTraits(decoder, valueOffset, ref traitsOrganization, ref traitsNumber);
					break;
				case "autonomous_system_organization":
					position = ReadOrganization(decoder, valueOffset, ref topOrganization);
					break;
				case "autonomous_system_number":
					position = ReadNumber(decoder, valueOffset, ref topNumber);
					break;
				case "as_name":
					position = ReadOrganization(decoder, valueOffset, ref infoOrganization);
					break;
				case "asn":
					position = ReadOrganization(decoder, valueOffset, ref infoAsn);
					break;
				default:
					position = decoder.SkipValue(valueOffset, 1);
					break;
			}
		}

		string isp = traitsOrganization.Length > 0 ? traitsOrganization
			: topOrganization.Length > 0 ? topOrganization
			: infoOrganization;
		if (isp.Length == 0)
		{
			ulong number = traitsNumber > 0 ? traitsNumber : topNumber;
			isp = number > 0 ? string.Concat("AS", number.ToString(CultureInfo.InvariantCulture)) : infoAsn;
		}

		GeoResult result = new(country, code, region, city, isp, source);
		return result.IsEmpty ? null : result;
	}

	/// <summary>读 map 的键（键恒为 UTF-8 字符串），position 推进到值处。</summary>
	private static string ReadKey(MmdbDecoder decoder, ref long position)
	{
		MmdbValue key = decoder.ReadValue(position, 1, out long next);
		if (key.Type != MmdbType.Utf8String)
		{
			throw new InvalidDataException("MMDB map key is not a string");
		}

		string name = decoder.ReadString(key);
		position = next;
		return name;
	}

	/// <summary>country 段：iso_code（国家码）+ names[language]（国家名）；IPinfo Lite 的 country 是纯字符串（无 names 映射），直接取为国家名。</summary>
	private static long ReadCountry(MmdbDecoder decoder, long offset, string language, ref string code, ref string country)
	{
		MmdbValue value = decoder.ReadValue(offset, 1, out long end);
		if (value.Type == MmdbType.Utf8String)
		{
			if (country.Length == 0)
			{
				country = decoder.ReadString(value);
			}

			return end;
		}

		if (value.Type != MmdbType.Map)
		{
			return end;
		}

		long position = value.Payload;
		for (int i = 0; i < value.Size; i++)
		{
			string name = ReadKey(decoder, ref position);
			if (name == "iso_code")
			{
				MmdbValue item = decoder.ReadValue(position, 1, out long next);
				position = next;
				if (code.Length == 0 && item.Type == MmdbType.Utf8String)
				{
					code = decoder.ReadString(item); // 与 ReadCountryCode 对称：已有国家码不覆盖
				}
			}
			else if (name == "names")
			{
				position = ReadNamesMap(decoder, position, language, ref country);
			}
			else
			{
				position = decoder.SkipValue(position, 1);
			}
		}

		return end;
	}

	/// <summary>
	/// names map（语言码 → 名称）选词：精确语言 → 同语言前缀（zh → zh-CN，GeoLite2/IPinfo 的中文名即 zh-CN）
	/// → 英文（en / en-*）→ 无（设计 §3.6 / R5.6：缺该语言回退英文）。
	/// </summary>
	private static long ReadNamesMap(MmdbDecoder decoder, long offset, string language, ref string field)
	{
		MmdbValue map = decoder.ReadValue(offset, 1, out long end);
		if (map.Type != MmdbType.Map)
		{
			return end;
		}

		string exact = "", prefixed = "", english = "";
		long position = map.Payload;
		for (int i = 0; i < map.Size; i++)
		{
			string name = ReadKey(decoder, ref position);
			MmdbValue value = decoder.ReadValue(position, 1, out long next);
			position = next;
			if (value.Type != MmdbType.Utf8String)
			{
				continue;
			}

			string text = decoder.ReadString(value);
			if (text.Length == 0)
			{
				continue;
			}

			if (exact.Length == 0 && MatchesLanguage(name, language, prefix: false))
			{
				exact = text;
			}
			else if (prefixed.Length == 0 && MatchesLanguage(name, language, prefix: true))
			{
				prefixed = text;
			}
			else if (english.Length == 0 && MatchesLanguage(name, "en", prefix: true))
			{
				english = text;
			}
		}

		field = exact.Length > 0 ? exact : prefixed.Length > 0 ? prefixed : english;
		return end;
	}

	/// <summary>subdivisions[0].names[language]（首个一级行政区 = 省/州）。</summary>
	private static long ReadFirstSubdivision(MmdbDecoder decoder, long offset, string language, ref string region)
	{
		MmdbValue array = decoder.ReadValue(offset, 1, out long end);
		if (array.Type != MmdbType.Array)
		{
			return end;
		}

		long position = array.Payload;
		for (int i = 0; i < array.Size; i++)
		{
			if (i == 0)
			{
				position = ReadNamesContainer(decoder, position, language, ref region);
			}
			else
			{
				position = decoder.SkipValue(position, 1);
			}
		}

		return end;
	}

	/// <summary>含 names 子键的容器 map（city / subdivisions[i]）：只取 names。</summary>
	private static long ReadNamesContainer(MmdbDecoder decoder, long offset, string language, ref string field)
	{
		MmdbValue map = decoder.ReadValue(offset, 1, out long end);
		if (map.Type != MmdbType.Map)
		{
			return end;
		}

		long position = map.Payload;
		for (int i = 0; i < map.Size; i++)
		{
			string name = ReadKey(decoder, ref position);
			if (name == "names")
			{
				position = ReadNamesMap(decoder, position, language, ref field);
			}
			else
			{
				position = decoder.SkipValue(position, 1);
			}
		}

		return end;
	}

	/// <summary>traits 段（GeoLite2-City / DB-IP City 系）：ASN 号（u16/u32/u64）与组织名。</summary>
	private static long ReadTraits(MmdbDecoder decoder, long offset, ref string organization, ref ulong number)
	{
		MmdbValue map = decoder.ReadValue(offset, 1, out long end);
		if (map.Type != MmdbType.Map)
		{
			return end;
		}

		long position = map.Payload;
		for (int i = 0; i < map.Size; i++)
		{
			string name = ReadKey(decoder, ref position);
			if (name == "autonomous_system_number")
			{
				position = ReadNumber(decoder, position, ref number);
			}
			else if (name == "autonomous_system_organization")
			{
				position = ReadOrganization(decoder, position, ref organization);
			}
			else
			{
				position = decoder.SkipValue(position, 1);
			}
		}

		return end;
	}

	/// <summary>读一个 ASN 号；非 u16/u32/u64 类型忽略。</summary>
	private static long ReadNumber(MmdbDecoder decoder, long offset, ref ulong number)
	{
		MmdbValue value = decoder.ReadValue(offset, 1, out long next);
		if (value.Type is MmdbType.Uint16 or MmdbType.Uint32 or MmdbType.Uint64)
		{
			number = value.Number;
		}

		return next;
	}

	/// <summary>读一个组织名 / ASN 文本（均恒为字符串）；非字符串忽略。</summary>
	private static long ReadOrganization(MmdbDecoder decoder, long offset, ref string field)
	{
		MmdbValue value = decoder.ReadValue(offset, 1, out long next);
		if (value.Type == MmdbType.Utf8String)
		{
			field = decoder.ReadString(value);
		}

		return next;
	}

	/// <summary>读一个字符串国家码（IPinfo Lite 顶层 country_code）；已有国家码时不覆盖（MaxMind 的 country.iso_code 优先）。</summary>
	private static long ReadCountryCode(MmdbDecoder decoder, long offset, ref string code)
	{
		MmdbValue value = decoder.ReadValue(offset, 1, out long next);
		if (code.Length == 0 && value.Type == MmdbType.Utf8String)
		{
			code = decoder.ReadString(value);
		}

		return next;
	}

	/// <summary>语言码匹配：精确（忽略大小写）或 BCP-47 区域前缀（前缀模式：zh 匹配 zh-CN）。</summary>
	private static bool MatchesLanguage(string name, string language, bool prefix)
	{
		if (language.Length == 0)
		{
			return false;
		}

		if (string.Equals(name, language, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return prefix
			&& name.Length > language.Length
			&& name[language.Length] == '-'
			&& name.AsSpan(0, language.Length).Equals(language, StringComparison.OrdinalIgnoreCase);
	}
}
