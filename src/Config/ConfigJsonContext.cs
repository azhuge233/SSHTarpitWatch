using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHTarpitWatch.Config;

/// <summary>System.Text.Json 源生成上下文（NativeAOT 安全）：宽松读——允许注释与尾逗号（设计 §4.1）。</summary>
[JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
