using System.Text.Json;

namespace SSHTarpitWatch.Config;

/// <summary>
/// 严格键校验（设计 §4.1 / R4.5）：按层级维护允许键集合，与原始 JSON 逐键比对——
/// ① 旧键名 → 逐条报错并给出迁移提示（`旧键 已改为 新键，请更新配置`）；
/// ② 任何未知键（含拼写错误）→ 报错。两者都不静默忽略（拼错的键名会静默关掉通知，属危险行为）。
/// 只看键名与层级：值类型错误仍由源生成反序列化路径报（宽松读选项与本校验一致——注释与尾逗号照常容忍）。
/// </summary>
internal static class ConfigKeyValidator
{
	/// <summary>宽松读选项（与 ConfigJsonContext 一致）。</summary>
	private static readonly JsonDocumentOptions options = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip,
	};

	/// <summary>校验原始 JSON 的键名；返回逐条错误（与文档路径无关，调用方负责补路径/退出码语义）。</summary>
	public static List<string> Validate(string json)
	{
		var errors = new List<string>();
		using JsonDocument document = JsonDocument.Parse(json, options);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
		{
			// 非对象根（null / 数组 / 标量）交给反序列化路径报错，这里不做键比对
			return errors;
		}

		Walk(document.RootElement, "", KeyScope.Root, errors);
		return errors;
	}

	private static void Walk(JsonElement obj, string prefix, KeyScope scope, List<string> errors)
	{
		foreach (JsonProperty property in obj.EnumerateObject())
		{
			string path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
			if (legacyKeys.TryGetValue((scope, property.Name), out string? newKey))
			{
				errors.Add($"{path} 已改为 {newKey}，请更新配置");
				continue;
			}

			if (!allowedKeys[scope].Contains(property.Name))
			{
				errors.Add($"{path} 不是已知配置键，请检查拼写");
				continue;
			}

			if (ChildScope(scope, property.Name) is KeyScope child && property.Value.ValueKind == JsonValueKind.Object)
			{
				Walk(property.Value, path, child, errors);
			}
		}
	}

	/// <summary>键所在层级——允许集合与迁移表都按层级定义（同名键在不同层级含义不同）。</summary>
	private enum KeyScope
	{
		Root,
		Server,
		Notifications,
		Telegram,
		Filter,
		Throttle,
		Geoip,
	}

	/// <summary>各层级的允许键集合（设计 §4.2 字段表）。</summary>
	private static readonly Dictionary<KeyScope, HashSet<string>> allowedKeys = new()
	{
		[KeyScope.Root] = ["nickname", "log_level", "server", "notifications"],
		[KeyScope.Server] = ["port", "bind_family", "delay_ms", "line_length", "max_clients"],
		[KeyScope.Notifications] = ["telegram", "filter", "throttle", "geoip"],
		[KeyScope.Telegram] = ["enabled", "bot_token", "chat_id"],
		[KeyScope.Filter] = ["on_connect", "on_close", "min_duration_seconds"],
		[KeyScope.Throttle] = ["per_ip_seconds", "max_per_minute"],
		// databases 是数组：Walk 只在值为 Object 时递归，数组天然不参与递归（无需特殊处理）
		[KeyScope.Geoip] = ["enabled", "databases", "language"],
	};

	/// <summary>旧键 → 新键（设计 §4.2 迁移表）；旧键出现即报错退出。</summary>
	private static readonly Dictionary<(KeyScope Scope, string Key), string> legacyKeys = new()
	{
		[(KeyScope.Root, "port")] = "server.port",
		[(KeyScope.Root, "bind_family")] = "server.bind_family",
		[(KeyScope.Root, "delay_ms")] = "server.delay_ms",
		[(KeyScope.Root, "line_length")] = "server.line_length",
		[(KeyScope.Root, "max_clients")] = "server.max_clients",
		[(KeyScope.Notifications, "notify_on_connect")] = "notifications.filter.on_connect",
		[(KeyScope.Notifications, "notify_on_close")] = "notifications.filter.on_close",
		[(KeyScope.Notifications, "dedup_per_ip_seconds")] = "notifications.throttle.per_ip_seconds",
		[(KeyScope.Notifications, "max_per_minute")] = "notifications.throttle.max_per_minute",
	};

	/// <summary>某个已知键的子层级（叶子键返回 null）。</summary>
	private static KeyScope? ChildScope(KeyScope scope, string key) => (scope, key) switch
	{
		(KeyScope.Root, "server") => KeyScope.Server,
		(KeyScope.Root, "notifications") => KeyScope.Notifications,
		(KeyScope.Notifications, "telegram") => KeyScope.Telegram,
		(KeyScope.Notifications, "filter") => KeyScope.Filter,
		(KeyScope.Notifications, "throttle") => KeyScope.Throttle,
		(KeyScope.Notifications, "geoip") => KeyScope.Geoip,
		_ => null,
	};
}
