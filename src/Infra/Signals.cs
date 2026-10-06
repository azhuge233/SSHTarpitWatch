using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SSHTarpitWatch.Infra;

/// <summary>
/// 信号监听（设计 §5）：回调只置位，主循环轮询处理。
/// SIGTERM/SIGINT/SIGHUP 走官方 PosixSignalRegistration；SIGUSR1 自注册（P/Invoke signal(10, handler)）。
/// </summary>
internal sealed class SignalWatcher : IDisposable
{
	/// <summary>Linux 上 SIGUSR1 恒为 10（x86-64 / arm64 / riscv64 同值）。</summary>
	private const int sigUsr1 = 10;

	/// <summary>SIGUSR1 置位标志：信号处理器只写它，主循环读并清零。</summary>
	private static int sigUsr1Pending;

	private readonly List<PosixSignalRegistration> registrations = [];
	private int terminateRequested;
	private int reloadRequested;

	public SignalWatcher()
	{
		Register(PosixSignal.SIGTERM, () => Interlocked.Exchange(ref terminateRequested, 1));
		Register(PosixSignal.SIGINT, () => Interlocked.Exchange(ref terminateRequested, 1));
		Register(PosixSignal.SIGHUP, () => Interlocked.Exchange(ref reloadRequested, 1));
		RegisterSigUsr1();
	}

	/// <summary>收到终止信号（SIGTERM/SIGINT）——主循环据此走优雅退出。</summary>
	public bool TerminateRequested => Volatile.Read(ref terminateRequested) != 0;

	/// <summary>收到 SIGHUP——主循环据此重载配置。</summary>
	public bool ReloadRequested => Volatile.Read(ref reloadRequested) != 0;

	/// <summary>主循环轮询：SIGUSR1 到达则返回 true（读取即清零）。</summary>
	public static bool ConsumeTotalsRequest() => Interlocked.Exchange(ref sigUsr1Pending, 0) != 0;

	/// <summary>一次重载处理完毕，允许下一次 SIGHUP 置位。</summary>
	public void AcknowledgeReload() => Interlocked.Exchange(ref reloadRequested, 0);

	public void Dispose()
	{
		foreach (PosixSignalRegistration registration in registrations)
		{
			registration.Dispose();
		}

		registrations.Clear();
	}

	/// <summary>置位式注册；平台不支持的信号只记 debug，不影响启动。</summary>
	private void Register(PosixSignal signal, Action onSignal)
	{
		try
		{
			registrations.Add(PosixSignalRegistration.Create(signal, context =>
			{
				context.Cancel = true;
				onSignal();
			}));
		}
		catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
		{
			Log.Debug($"SIGNAL signal={signal} unsupported: {ex.Message}");
		}
	}

	/// <summary>SIGUSR1 自注册：.NET 运行时不用 USR1（用 SIGRTMIN 作激活信号），实测可行（设计 §5）。</summary>
	private unsafe void RegisterSigUsr1()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		try
		{
			sigUsr1Pending = 0; // 先在托管上下文触碰该字段，确保类型已初始化
			IntPtr handler = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnSigUsr1;
			IntPtr previous = Signal(sigUsr1, handler);
			Log.Debug($"SIGNAL signal=SIGUSR1({sigUsr1}) registered previous=0x{previous.ToInt64():X}");
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			Log.Debug($"SIGNAL signal=SIGUSR1 unsupported: {ex.Message}");
		}
	}

	/// <summary>信号处理器：只置位，不做任何其它事（分配/加锁都会引入风险）。</summary>
	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static void OnSigUsr1(int signalNumber) => sigUsr1Pending = 1;

	/// <summary>libc 的 signal()：返回上一个处理器（SIG_DFL=0 / SIG_IGN=1 / 其它为函数地址）。</summary>
	[DllImport("libc", EntryPoint = "signal", SetLastError = true)]
	private static extern IntPtr Signal(int signum, IntPtr handler);
}
