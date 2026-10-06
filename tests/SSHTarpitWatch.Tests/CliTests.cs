using Microsoft.VisualStudio.TestTools.UnitTesting;
using SSHTarpitWatch.Infra;

namespace SSHTarpitWatch.Tests;

/// <summary>命令行解析（设计 §4.4 / §7）：--config 两种形式与默认路径、三个标志位、用法错误。</summary>
[TestClass]
public sealed class CliTests
{
	[TestMethod]
	public void Parse_PositiveCases_ConfigPathFormsAndFlags()
	{
		// 无参数：默认 = 二进制所在目录的 config.json（设计 §4.1），无用法错误
		CliOptions none = CliOptions.Parse([]);
		Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "config.json"), none.ConfigPath, "无参数默认 = 二进制所在目录的 config.json");
		Assert.IsNull(none.Error);
		Assert.IsFalse(none.ShowHelp || none.ShowVersion || none.TestNotify);

		// --config <路径> 与 --config=<路径> 等价
		CliOptions separated = CliOptions.Parse(["--config", "/tmp/a.json"]);
		Assert.AreEqual("/tmp/a.json", separated.ConfigPath, "--config <路径>");
		Assert.IsNull(separated.Error);

		CliOptions inline = CliOptions.Parse(["--config=/tmp/b.json"]);
		Assert.AreEqual("/tmp/b.json", inline.ConfigPath, "--config=<路径>");
		Assert.IsNull(inline.Error);

		// 三个标志位
		CliOptions flags = CliOptions.Parse(["--help", "--version", "--test-notify"]);
		Assert.IsTrue(flags.ShowHelp, "--help");
		Assert.IsTrue(flags.ShowVersion, "--version");
		Assert.IsTrue(flags.TestNotify, "--test-notify");
		Assert.IsNull(flags.Error);
		Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "config.json"), flags.ConfigPath, "标志位不影响默认配置路径");
	}

	[TestMethod]
	public void Parse_Errors_UnknownOptionAndMissingConfigArgument()
	{
		CliOptions unknown = CliOptions.Parse(["--bogus"]);
		Assert.IsNotNull(unknown.Error, "未知参数应记用法错误");
		StringAssert.Contains(unknown.Error ?? "", "unknown option: --bogus");

		CliOptions missing = CliOptions.Parse(["--config"]);
		Assert.IsNotNull(missing.Error, "--config 缺参数应记用法错误");
		StringAssert.Contains(missing.Error ?? "", "requires a path");
	}
}
