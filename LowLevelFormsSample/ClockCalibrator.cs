using System.Diagnostics;
using Evergine.Common.Graphics;

namespace LowLevelFormsSample
{
	/// <summary>
	/// Turns <see cref="CommandQueue.GetClockCalibration"/> samples into what Tracy's calibration
	/// event wants: the raw GPU timestamp of the pair, and how far the CPU clock advanced since the
	/// previous pair, in nanoseconds. The CPU half comes back in <see cref="Stopwatch"/> ticks, so
	/// the scaling is the one constant this class exists to keep in a single place.
	/// </summary>
	internal sealed class ClockCalibrator
	{
		private readonly CommandQueue queue;
		private readonly double nanosecondsPerTick = 1e9 / Stopwatch.Frequency;
		private long previousCpuTicks;

		/// <param name="queue">The queue the timestamps are recorded on.</param>
		/// <param name="initialCpuTicks">The CPU half of the pair the Tracy context was created with.</param>
		public ClockCalibrator(CommandQueue queue, long initialCpuTicks)
		{
			this.queue = queue;
			this.previousCpuTicks = initialCpuTicks;
		}

		/// <summary>
		/// Samples a new pair. Returns false when the backend refused the sample or the CPU clock did
		/// not move, in which case nothing should be emitted: the server divides by the deltas.
		/// </summary>
		public bool TrySample(out long gpuTimestamp, out long cpuDeltaNanoseconds)
		{
			if (!this.queue.GetClockCalibration(out ulong gpu, out long cpu))
			{
				gpuTimestamp = 0;
				cpuDeltaNanoseconds = 0;
				return false;
			}

			gpuTimestamp = (long)gpu;
			cpuDeltaNanoseconds = (long)((cpu - this.previousCpuTicks) * this.nanosecondsPerTick);
			this.previousCpuTicks = cpu;
			return cpuDeltaNanoseconds > 0;
		}
	}
}
