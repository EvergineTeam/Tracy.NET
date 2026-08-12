using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Evergine.Bindings.Tracy
{
	/// <summary>
	/// A GPU profiling track, agnostic of the graphics API. The caller owns the timestamp
	/// queries — with Evergine's low-level layer that means a QueryHeap of type Timestamp,
	/// CommandBuffer.WriteTimestamp per zone edge, and QueryHeap.ReadData after the frame —
	/// and this class owns the emission protocol: context creation, zone events at record
	/// time, and the timestamps once they are read back.
	///
	/// The query ids this class hands out are plain ring indices. Size the query heap with
	/// the same capacity and the mapping between heap slots and Tracy query ids is identity:
	/// ReadData(i, ...) feeds SubmitTime(i, ...) with no bookkeeping in between.
	/// </summary>
	public sealed unsafe class GpuProfilerContext
	{
		// Context ids are process-global bytes, exactly like the C++ helpers' GetGpuCtxCounter.
		private static int nextContextId = -1;

		private readonly byte context;
		private readonly ushort queryCapacity;
		private int nextQueryId;

		private GpuProfilerContext(byte context, ushort queryCapacity)
		{
			this.context = context;
			this.queryCapacity = queryCapacity;
		}

		/// <summary>
		/// Creates a GPU context. <paramref name="initialGpuTimestamp"/> is one raw timestamp
		/// read from the GPU at startup (write one query, wait, read it back), and
		/// <paramref name="periodNs"/> converts GPU ticks to nanoseconds — with Evergine's
		/// low-level layer, <c>1e9f / graphicsContext.TimestampFrequency</c>.
		/// </summary>
		public static GpuProfilerContext Create(
			string name,
			TracyGpuContextType type,
			long initialGpuTimestamp,
			float periodNs,
			ushort queryCapacity = 64)
		{
			int id = Interlocked.Increment(ref nextContextId);
			if (id > byte.MaxValue)
			{
				throw new InvalidOperationException("Tracy supports at most 256 GPU contexts per process.");
			}

			var ctx = new GpuProfilerContext((byte)id, queryCapacity);

			Tracy.___tracy_emit_gpu_new_context_serial(new ___tracy_gpu_new_context_data
			{
				gpuTime = initialGpuTimestamp,
				period = periodNs,
				context = ctx.context,
				// No calibration flag: Evergine's low-level layer exposes no CPU-GPU clock
				// correlation, so the track is aligned through TimeSync instead — the same
				// scheme upstream's OpenGL helper uses.
				flags = 0,
				type = (byte)type,
			});

			if (!string.IsNullOrEmpty(name))
			{
				// Verified against TracyProfiler.cpp at v0.14.0: ___tracy_emit_gpu_context_name
				// copies the buffer (tracy_malloc + memcpy) during the call, so transient
				// UTF-8 is safe here — unlike frame and plot names, which tracy retains.
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
		/// </summary>
		public GpuZone BeginZone(
			string name = null,
			uint color = 0,
			[CallerLineNumber] int line = 0,
			[CallerFilePath] string file = "",
			[CallerMemberName] string member = "")
		{
			ushort beginId = this.NextQueryId();
			ushort endId = this.NextQueryId();

			// The non-alloc variant stores the pointer, so the source location must be
			// static storage — which the process-lifetime intern table is.
			var srcloc = Profiler.GetSourceLocation(file, member, line, name, color);

			Tracy.___tracy_emit_gpu_zone_begin_serial(new ___tracy_gpu_zone_begin_data
			{
				srcloc = (ulong)srcloc,
				queryId = beginId,
				context = this.context,
			});

			return new GpuZone(this, beginId, endId);
		}

		internal void EndZone(ushort endQueryId)
		{
			Tracy.___tracy_emit_gpu_zone_end_serial(new ___tracy_gpu_zone_end_data
			{
				queryId = endQueryId,
				context = this.context,
			});
		}

		/// <summary>
		/// Delivers one read-back GPU timestamp for a query id previously handed out by
		/// <see cref="BeginZone"/>. Order does not matter to Tracy; completeness does — a
		/// zone whose two timestamps never arrive stays open in the capture.
		/// </summary>
		public void SubmitTime(ushort queryId, long gpuTime)
		{
			Tracy.___tracy_emit_gpu_time_serial(new ___tracy_gpu_time_data
			{
				gpuTime = gpuTime,
				queryId = queryId,
				context = this.context,
			});
		}

		/// <summary>
		/// Re-anchors the GPU clock against the CPU timeline. Call every few hundred frames
		/// with a fresh raw GPU timestamp to keep an uncalibrated track from drifting.
		/// </summary>
		public void TimeSync(long gpuTime)
		{
			Tracy.___tracy_emit_gpu_time_sync_serial(new ___tracy_gpu_time_sync_data
			{
				gpuTime = gpuTime,
				context = this.context,
			});
		}

		private ushort NextQueryId()
		{
			// A ring, not a counter: ids repeat once the capacity wraps, which is fine as
			// long as the consumer drains (SubmitTime) faster than it emits. That contract
			// is the consumer's query-heap size — hence capacity comes from Create.
			int raw = Interlocked.Increment(ref this.nextQueryId);
			return (ushort)(raw % this.queryCapacity);
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
