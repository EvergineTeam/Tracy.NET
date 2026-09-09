using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Evergine.Bindings.Tracy
{
	/// <summary>
	/// A GPU profiling track, agnostic of the graphics API. The caller owns the timestamp
	/// queries (with Evergine's low-level layer that means a QueryHeap of type Timestamp,
	/// CommandBuffer.WriteTimestamp per zone edge, and QueryHeap.ReadData after the frame),
	/// and this class owns the emission protocol: context creation, zone events at record
	/// time, and the timestamps once they are read back.
	///
	/// The query ids this class hands out are plain ring indices. Size the query heap with
	/// the same capacity and the mapping between heap slots and Tracy query ids is identity:
	/// ReadData(i, ...) feeds SubmitTime(i, ...) with no bookkeeping in between. Pairs are
	/// aligned to even ids, so a zone's begin and end are always adjacent slots.
	///
	/// A context is aligned with the CPU timeline in one of two ways. A <em>calibrated</em>
	/// context is created from a pair of GPU and CPU timestamps sampled at the same instant
	/// (Evergine: <c>CommandQueue.GetClockCalibration</c>) and re-anchored with
	/// <see cref="Calibrate"/>; the viewer then maps every GPU timestamp through that pair
	/// and the measured ratio of both clocks. An <em>uncalibrated</em> context is anchored on
	/// whatever GPU timestamp it was created with and re-anchored with <see cref="TimeSync"/>,
	/// which is only as good as the simultaneity of the sample handed to it.
	/// </summary>
	public sealed unsafe class GpuProfilerContext
	{
		// Context ids are process-global bytes, exactly like the C++ helpers' GetGpuCtxCounter.
		private static int nextContextId = -1;

		private readonly byte context;
		private readonly ushort queryCapacity;
		private readonly bool[] emitted;

		// -1, like nextContextId: Interlocked.Increment returns the incremented value, so the
		// first id handed out is 0 and pairs land on (0,1), (2,3)... Starting at 0 would hand
		// out (1,2) and eventually (capacity-1, 0), a pair straddling the ring wrap.
		private int nextQueryId = -1;
		private long lastCalibrationGpuTime;

		private GpuProfilerContext(byte context, ushort queryCapacity, bool calibrated)
		{
			this.context = context;
			this.queryCapacity = queryCapacity;
			this.emitted = new bool[queryCapacity];
			this.IsCalibrated = calibrated;
		}

		/// <summary>
		/// Gets a value indicating whether this context was created from a simultaneous GPU/CPU
		/// timestamp pair and is kept aligned through <see cref="Calibrate"/>.
		/// </summary>
		public bool IsCalibrated { get; }

		/// <summary>
		/// Creates an uncalibrated GPU context. <paramref name="initialGpuTimestamp"/> is one raw
		/// timestamp read from the GPU at startup (write one query, wait, read it back), and
		/// <paramref name="periodNs"/> converts GPU ticks to nanoseconds. With Evergine's
		/// low-level layer, <c>1e9f / graphicsContext.TimestampFrequency</c>.
		/// </summary>
		public static GpuProfilerContext Create(
			string name,
			TracyGpuContextType type,
			long initialGpuTimestamp,
			float periodNs,
			ushort queryCapacity = 64)
		{
			return Create(name, type, initialGpuTimestamp, periodNs, queryCapacity, calibrated: false);
		}

		/// <summary>
		/// Creates a GPU context, calibrated or not.
		/// </summary>
		/// <remarks>
		/// Tracy stamps the CPU side of the initial anchor itself, with its own clock, at the
		/// moment this method runs. For a calibrated context that means
		/// <paramref name="initialGpuTimestamp"/> must be the GPU half of a pair sampled
		/// <em>immediately</em> before the call: the CPU half is implied by "now", and every
		/// microsecond between the sample and this call becomes a constant offset of the track.
		/// <paramref name="queryCapacity"/> has to be even so begin/end pairs never straddle
		/// the ring wrap.
		/// </remarks>
		public static GpuProfilerContext Create(
			string name,
			TracyGpuContextType type,
			long initialGpuTimestamp,
			float periodNs,
			ushort queryCapacity,
			bool calibrated)
		{
			if (queryCapacity < 2 || (queryCapacity % 2) != 0)
			{
				throw new ArgumentException("The query capacity must be an even number of at least 2, so zone begin/end pairs never straddle the ring wrap.", nameof(queryCapacity));
			}

			int id = Interlocked.Increment(ref nextContextId);
			if (id > byte.MaxValue)
			{
				throw new InvalidOperationException("Tracy supports at most 256 GPU contexts per process.");
			}

			var ctx = new GpuProfilerContext((byte)id, queryCapacity, calibrated);
			ctx.lastCalibrationGpuTime = initialGpuTimestamp;

			Tracy.___tracy_emit_gpu_new_context_serial(new ___tracy_gpu_new_context_data
			{
				gpuTime = initialGpuTimestamp,
				period = periodNs,
				context = ctx.context,
				// GpuContextCalibration (1 << 0) from tracy::GpuContextFlags in TracyQueue.hpp:
				// tells the server to map GPU timestamps through the calibration pairs instead
				// of the fixed offset TimeSync maintains.
				flags = calibrated ? (byte)1 : (byte)0,
				type = (byte)type,
			});

			if (!string.IsNullOrEmpty(name))
			{
				// Verified against TracyProfiler.cpp at v0.14.0: ___tracy_emit_gpu_context_name
				// copies the buffer (tracy_malloc + memcpy) during the call, so transient
				// UTF-8 is safe here, unlike frame and plot names, which tracy retains.
				var bytes = Encoding.UTF8.GetBytes(name);
				fixed (byte* ptr = bytes)
				{
					Tracy.___tracy_emit_gpu_context_name_serial(new ___tracy_gpu_context_name_data
					{
						context = ctx.context,
						name = ptr,
						len = (ushort)bytes.Length,
					});
				}
			}

			return ctx;
		}

		/// <summary>
		/// Opens a GPU zone at command-recording time and reserves two query ids: write the
		/// GPU timestamp for <see cref="GpuZone.BeginQueryId"/> now, the one for
		/// <see cref="GpuZone.EndQueryId"/> where the zone ends, and call
		/// <see cref="GpuZone.End"/> after it.
		///
		/// There is no per-instance recolor here the way <see cref="ProfilerZone.Color"/>
		/// offers one: a GPU zone is not on any thread's zone stack, so its color can only
		/// travel in the source location.
		/// </summary>
		public GpuZone BeginZone(
			string name = null,
			TracyColor color = TracyColor.None,
			[CallerLineNumber] int line = 0,
			[CallerFilePath] string file = "",
			[CallerMemberName] string member = "")
		{
			// The client records continuously (the natives are built without
			// TRACY_ON_DEMAND, see binding.yml for why), so zones are always emitted and
			// history from before the viewer connects is preserved. The emitted[] array is
			// bookkeeping, not gating: it pairs each SubmitTime with exactly one zone edge,
			// so a stale or duplicated readback cannot send a second time for the same id.
			ushort beginId = this.NextQueryId();
			ushort endId = this.NextQueryId();

			const bool active = true;
			this.emitted[beginId] = active;
			this.emitted[endId] = active;

			if (active)
			{
				// Alloc path, same reason as CPU zones: the client copies the source
				// location content inline instead of the server resolving a pointer that
				// managed-owned memory can never satisfy.
				var srcloc = Profiler.AllocSourceLocation(file, member, line, name, color);

				Tracy.___tracy_emit_gpu_zone_begin_alloc_serial(new ___tracy_gpu_zone_begin_data
				{
					srcloc = srcloc,
					queryId = beginId,
					context = this.context,
				});
			}

			return new GpuZone(this, beginId, endId);
		}

		internal void EndZone(ushort endQueryId)
		{
			if (!this.emitted[endQueryId])
			{
				return;
			}

			Tracy.___tracy_emit_gpu_zone_end_serial(new ___tracy_gpu_zone_end_data
			{
				queryId = endQueryId,
				context = this.context,
			});
		}

		/// <summary>
		/// Delivers one read-back GPU timestamp for a query id previously handed out by
		/// <see cref="BeginZone"/>. Order does not matter to Tracy; completeness does, since a
		/// zone whose two timestamps never arrive stays open in the capture. Times for ids
		/// whose zone events were not emitted (no server connected at the time) are dropped
		/// here, so the consumer never needs to track connection state itself.
		/// </summary>
		public void SubmitTime(ushort queryId, long gpuTime)
		{
			if (!this.emitted[queryId])
			{
				return;
			}

			this.emitted[queryId] = false;

			Tracy.___tracy_emit_gpu_time_serial(new ___tracy_gpu_time_data
			{
				gpuTime = gpuTime,
				queryId = queryId,
				context = this.context,
			});
		}

		/// <summary>
		/// Re-anchors an uncalibrated GPU clock against the CPU timeline. Tracy stamps the CPU
		/// side as "now", so <paramref name="gpuTime"/> must be a GPU timestamp that is current
		/// at the moment of the call, since a timestamp read back from a previous frame would shift
		/// the whole track by the age of that frame. No-op on a calibrated context, where the
		/// server ignores the offset this maintains.
		/// </summary>
		public void TimeSync(long gpuTime)
		{
			if (this.IsCalibrated)
			{
				return;
			}

			Tracy.___tracy_emit_gpu_time_sync_serial(new ___tracy_gpu_time_sync_data
			{
				gpuTime = gpuTime,
				context = this.context,
			});
		}

		/// <summary>
		/// Re-anchors a calibrated context on a fresh simultaneous pair. <paramref name="gpuTimestamp"/>
		/// is the raw GPU half of the pair and <paramref name="cpuDeltaNanoseconds"/> is how far the
		/// CPU half advanced since the pair the context was created with, or since the previous call,
		/// measured with the clock the pair came from (Evergine: <c>Stopwatch</c> ticks, scaled to
		/// nanoseconds). Tracy stamps its own CPU clock as "now", so call this immediately after
		/// sampling the pair; the server derives the ratio of both clocks from the two deltas.
		/// </summary>
		/// <remarks>
		/// Calls with a non-positive CPU delta or an unchanged GPU timestamp are dropped: the server
		/// divides by the GPU delta. Every few hundred frames is plenty; the drift being corrected is
		/// parts per million.
		/// </remarks>
		public void Calibrate(long gpuTimestamp, long cpuDeltaNanoseconds)
		{
			if (!this.IsCalibrated)
			{
				throw new InvalidOperationException("Calibrate is only valid on a context created with calibrated: true; uncalibrated contexts are re-anchored with TimeSync.");
			}

			if (cpuDeltaNanoseconds <= 0 || gpuTimestamp == this.lastCalibrationGpuTime)
			{
				return;
			}

			this.lastCalibrationGpuTime = gpuTimestamp;

			Tracy.___tracy_emit_gpu_calibration_serial(new ___tracy_gpu_calibration_data
			{
				gpuTime = gpuTimestamp,
				cpuDelta = cpuDeltaNanoseconds,
				context = this.context,
			});
		}

		private ushort NextQueryId()
		{
			// A ring, not a counter: ids repeat once the capacity wraps, which is fine as
			// long as the consumer drains (SubmitTime) faster than it emits. That contract
			// is the consumer's query-heap size, hence capacity comes from Create. Unsigned
			// before the modulo so a wrapped counter can never produce a negative index.
			int raw = Interlocked.Increment(ref this.nextQueryId);
			return (ushort)((uint)raw % this.queryCapacity);
		}
	}

	/// <summary>
	/// An open GPU zone: two reserved query ids and the obligation to call <see cref="End"/>
	/// once the closing timestamp has been recorded into the command buffer.
	/// </summary>
	public readonly struct GpuZone
	{
		private readonly GpuProfilerContext owner;

		/// <summary>Query id whose timestamp marks where the zone starts.</summary>
		public ushort BeginQueryId { get; }

		/// <summary>Query id whose timestamp marks where the zone ends.</summary>
		public ushort EndQueryId { get; }

		internal GpuZone(GpuProfilerContext owner, ushort beginQueryId, ushort endQueryId)
		{
			this.owner = owner;
			this.BeginQueryId = beginQueryId;
			this.EndQueryId = endQueryId;
		}

		/// <summary>Emits the zone-end event. Call it where the zone closes in the command stream.</summary>
		public void End()
		{
			this.owner.EndZone(this.EndQueryId);
		}
	}
}
