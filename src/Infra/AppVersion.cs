using System.Reflection;

namespace SSHTarpitWatch.Infra;

/// <summary>
/// 版本号的单一出处 = csproj 的 &lt;Version&gt;（运行时经程序集元数据读取，NativeAOT 安全，设计 §4.4）。
/// 首选 AssemblyInformationalVersion（去掉构建元数据后缀 +sha），读不到则回退程序集版本。
/// </summary>
internal static class AppVersion
{
	private static readonly string current = Resolve();

	/// <summary>形如 0.1.0。</summary>
	public static string Current => current;

	/// <summary>--version 的整行输出。</summary>
	public static string Line => $"SSHTarpitWatch {current}";

	private static string Resolve()
	{
		Assembly assembly = typeof(AppVersion).Assembly;
		string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		if (!string.IsNullOrEmpty(informational))
		{
			int plus = informational.IndexOf('+', StringComparison.Ordinal);
			return plus >= 0 ? informational[..plus] : informational;
		}

		Version? version = assembly.GetName().Version;
		if (version is null)
		{
			return "0.0.0";
		}

		return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
	}
}
