namespace SSHTarpitWatch.Tarpit;

/// <summary>
/// 容量门控（设计 §2.1）：以 SemaphoreSlim 计空位，满员时 accept 循环先等待——
/// 新连接滞留内核 backlog（对齐 endlessh 满员行为）。
/// 热重载（§4.3）：放大即时生效（补发空位）；缩小在连接释放时收敛到新值。
/// </summary>
internal sealed class CapacityGate
{
	private readonly object sync = new();
	private readonly SemaphoreSlim semaphore;
	private readonly TimeSpan drainWait = TimeSpan.FromSeconds(1);
	private int currentMax;
	private int pendingRemovals;
	private bool draining;

	public CapacityGate(int maxClients)
	{
		currentMax = maxClients;
		semaphore = new SemaphoreSlim(maxClients, int.MaxValue);
	}

	/// <summary>当前并发上限（热重载可改）。</summary>
	public int CurrentMax => Volatile.Read(ref currentMax);

	/// <summary>占用一个空位；无空位则等待。</summary>
	public Task AcquireAsync(CancellationToken token) => semaphore.WaitAsync(token);

	/// <summary>释放一个空位（连接收盘时调用）。</summary>
	public void Release() => semaphore.Release();

	/// <summary>按新上限调整门控。</summary>
	public void Resize(int newMax)
	{
		lock (sync)
		{
			int delta = newMax - currentMax;
			if (delta == 0)
			{
				return;
			}

			currentMax = newMax;
			if (delta > 0)
			{
				pendingRemovals = 0; // 取消进行中的回收
				semaphore.Release(delta);
				return;
			}

			pendingRemovals += -delta;
			if (!draining)
			{
				draining = true;
				_ = DrainAsync();
			}
		}
	}

	/// <summary>回收多余空位：阻塞等待，直到有连接释放空位为止——上限随之收敛。</summary>
	private async Task DrainAsync()
	{
		while (true)
		{
			lock (sync)
			{
				if (pendingRemovals <= 0)
				{
					draining = false;
					return;
				}
			}

			bool taken = await semaphore.WaitAsync(drainWait).ConfigureAwait(false);
			if (!taken)
			{
				continue;
			}

			lock (sync)
			{
				if (pendingRemovals <= 0)
				{
					semaphore.Release();
					draining = false;
					return;
				}

				pendingRemovals--;
			}
		}
	}
}
