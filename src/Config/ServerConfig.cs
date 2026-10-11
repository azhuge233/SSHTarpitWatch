using System.Text.Json.Serialization;

namespace SSHTarpitWatch.Config;

/// <summary>绑定地址族（config 的 server.bind_family）。</summary>
internal enum BindFamilyMode
{
	Auto,
	Ipv4,
	Ipv6,
}

/// <summary>
/// 监听与焦油坑行为配置（设计 §4.2 的 server.* 段）：port / bind_family / delay_ms / line_length / max_clients。
/// 全部键带默认值：段缺失或键缺失时按默认值运行。
/// 注意：Validate 之前必须先 Normalize——JSON 里显式写 null 会把非空属性置空。
/// </summary>
internal sealed class ServerConfig
{
	[JsonPropertyName("port")]
	public int Port { get; set; } = 2222;

	[JsonPropertyName("bind_family")]
	public string BindFamily { get; set; } = "auto";

	[JsonPropertyName("delay_ms")]
	public int DelayMs { get; set; } = 10000;

	[JsonPropertyName("line_length")]
	public int LineLength { get; set; } = 32;

	[JsonPropertyName("max_clients")]
	public int MaxClients { get; set; } = 4096;

	/// <summary>解析后的绑定地址族；非法值由 Validate 报错，这里按 auto 兜底。</summary>
	[JsonIgnore]
	public BindFamilyMode FamilyMode => BindFamily switch
	{
		"ipv4" => BindFamilyMode.Ipv4,
		"ipv6" => BindFamilyMode.Ipv6,
		_ => BindFamilyMode.Auto,
	};

	/// <summary>JSON null 兜底（源生成直接赋值，可能把声明为非空的属性置 null）。</summary>
	public void Normalize() => BindFamily ??= "auto";

	/// <summary>校验矩阵（设计 §4.2）；错误追加进 <paramref name="errors"/>。</summary>
	public void Validate(List<string> errors)
	{
		if (Port is < 1 or > 65535)
		{
			errors.Add($"server.port={Port} is out of range 1..65535");
		}

		if (BindFamily is not ("auto" or "ipv4" or "ipv6"))
		{
			errors.Add($"server.bind_family=\"{BindFamily}\" must be one of auto, ipv4, ipv6");
		}

		if (DelayMs < 1)
		{
			errors.Add($"server.delay_ms={DelayMs} must be >= 1");
		}

		if (LineLength is < 3 or > 255)
		{
			errors.Add($"server.line_length={LineLength} is out of range 3..255");
		}

		if (MaxClients < 1)
		{
			errors.Add($"server.max_clients={MaxClients} must be >= 1");
		}
	}
}
