namespace SSHTarpitWatch.Tests;

/// <summary>
/// 假时钟（设计 §3.5/§7 测试缝）：时间只在 <see cref="Advance"/> 时推进；
/// Task.Delay / ITimer 按推进量触发——闸门冷却与退避序列可在毫秒内确定性地验证。
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
	private readonly object sync = new();
	private readonly List<FakeTimer> timers = [];
	private readonly TimeZoneInfo zone;
	private DateTimeOffset utcNow;

	public FakeTimeProvider(DateTimeOffset start, TimeZoneInfo? localZone = null)
	{
		utcNow = start;
		zone = localZone ?? TimeZoneInfo.Utc;
	}

	public override TimeZoneInfo LocalTimeZone => zone;

	/// <summary>尚未触发（且未释放）的定时器数：测试用它等待“工作器已进入等待”。</summary>
	public int ActiveTimerCount
	{
		get
		{
			lock (sync)
			{
				return timers.Count(timer => !timer.Fired && !timer.Disposed);
			}
		}
	}

	public override DateTimeOffset GetUtcNow()
	{
		lock (sync)
		{
			return utcNow;
		}
	}

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public override long GetTimestamp()
	{
		lock (sync)
		{
			return utcNow.UtcTicks;
		}
	}

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		FakeTimer timer = new(this, callback, state, period, GetUtcNow() + dueTime);
		lock (sync)
		{
			timers.Add(timer);
		}

		return timer;
	}

	/// <summary>推进时间并触发到期定时器（一次性定时器触发一次；周期定时器按周期补齐）。</summary>
	public void Advance(TimeSpan delta)
	{
		List<(TimerCallback Callback, object? State)> due = [];
		lock (sync)
		{
			utcNow += delta;
			foreach (FakeTimer timer in timers)
			{
				if (timer.Fired || timer.Disposed)
				{
					continue;
				}

				while (!timer.Disposed && timer.DueAt <= utcNow)
				{
					due.Add((timer.Callback, timer.State));
					if (timer.Period <= TimeSpan.Zero) // InfiniteTimeSpan = -1ms：一次性
					{
						timer.Fired = true;
						break;
					}

					timer.DueAt += timer.Period;
				}
			}

			timers.RemoveAll(timer => timer.Disposed);
		}

		foreach ((TimerCallback callback, object? state) in due)
		{
			callback(state);
		}
	}

	private sealed class FakeTimer : ITimer
	{
		private readonly FakeTimeProvider owner;

		internal FakeTimer(
			FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan period, DateTimeOffset dueAt)
		{
			this.owner = owner;
			Callback = callback;
			State = state;
			Period = period;
			DueAt = dueAt;
		}

		internal TimerCallback Callback { get; }

		internal object? State { get; }

		internal TimeSpan Period { get; }

		internal DateTimeOffset DueAt { get; set; }

		internal bool Fired { get; set; }

		internal bool Disposed { get; set; }

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			lock (owner.sync)
			{
				if (Disposed)
				{
					return false;
				}

				DueAt = owner.utcNow + dueTime;
				Fired = false;
				return true;
			}
		}

		public void Dispose()
		{
			lock (owner.sync)
			{
				Disposed = true;
			}
		}

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
