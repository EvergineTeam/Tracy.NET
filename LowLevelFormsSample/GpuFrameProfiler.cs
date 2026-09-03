using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Evergine.Bindings.Tracy;
using Evergine.Common.Graphics;

namespace LowLevelFormsSample
{
	/// <summary>
	/// Puts real GPU timestamps behind <see cref="GpuProfilerContext"/>. The binding owns the
	/// emission protocol and deliberately owns nothing else — no query pool, no readback, no
	/// driver — so this class is the other half of the contract, written against Evergine's
	/// low-level graphics API exactly as the repository README describes it.
	///
	/// The whole design rests on one identity: <see cref="GpuProfilerContext"/> hands out query
	/// ids as plain ring indices modulo its capacity, so a <see cref="QueryHeap"/> created with
	/// the same capacity maps slot to id with no translation at all.
	/// </summary>
	internal sealed class GpuFrameProfiler : IDisposable
	{
		/// <summary>
		/// Shared by the Tracy context and the query heap — that is what makes ids and heap slots
		/// the same number. Two zones per frame use four ids, so 64 leaves ample headroom over
		/// the one frame of readback latency.
		/// </summary>
		private const ushort Capacity = 64;

		/// <summary>Re-anchor the uncalibrated GPU clock roughly every five seconds at 60 fps.</summary>
		private const int SyncEveryFrames = 300;

		private readonly QueryHeap queryHeap;
		private readonly GpuProfilerContext context;
		private readonly ulong[] results = new ulong[Capacity];
		private readonly List<(ushort Begin, ushort End)> pending = new();
		private readonly double millisecondsPerTick;

		private long lastRawTimestamp;
		private int framesSinceSync;

		private GpuFrameProfiler(QueryHeap queryHeap, GpuProfilerContext context, double millisecondsPerTick)
		{
			this.queryHeap = queryHeap;
			this.context = context;
			this.millisecondsPerTick = millisecondsPerTick;
		}

		/// <summary>
		/// Gets the wall time of the outermost GPU zone of the last drained frame, in milliseconds.
		/// Purely for the on-screen status bar: it is the same span the viewer draws, which makes
		/// disagreement between the two easy to spot.
		/// </summary>
		public double LastFrameMilliseconds { get; private set; }

		/// <summary>
		/// Creates the query heap and the Tracy context. Establishing the context needs one raw
		/// GPU timestamp, so this submits a command buffer that does nothing but write one and
		/// waits for it — a one-off cost at startup, before any frame exists.
		/// </summary>
		public static GpuFrameProfiler Create(GraphicsContext graphicsContext, CommandQueue commandQueue, string name)
		{
			var heapDescription = new QueryHeapDescription()
			{
				Type = QueryType.Timestamp,
				QueryCount = Capacity,
			};

			var queryHeap = graphicsContext.Factory.CreateQueryHeap(ref heapDescription);

			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();

			// Slot 0 is inside the ring and will be reused later; that is harmless, because this
			// value is read exactly once, here, before any zone has been emitted.
			commandBuffer.WriteTimestamp(queryHeap, 0);
			commandBuffer.End();
			commandBuffer.Commit();
			commandQueue.Submit();
			commandQueue.WaitIdle();

			var initial = new ulong[Capacity];
			queryHeap.ReadData(0, 1, initial);

			// Ticks to nanoseconds, which is the unit Tracy's period is expressed in.
			float periodNs = 1e9f / graphicsContext.TimestampFrequency;

			var context = GpuProfilerContext.Create(
				name,
				TracyGpuContextType.Direct3D12,
				(long)initial[0],
				periodNs,
				Capacity);

			return new GpuFrameProfiler(queryHeap, context, periodNs / 1e6);
		}

		/// <summary>
		/// Opens a GPU zone and records its opening timestamp into the command buffer.
		/// </summary>
		/// <remarks>
		/// The caller attributes are forwarded rather than defaulted: without this every GPU zone
		/// in the capture would report its source location as this line of this file instead of
		/// the call site that actually opened it.
		/// </remarks>
		public GpuZone BeginZone(
			CommandBuffer commandBuffer,
			string name,
			TracyColor color = TracyColor.None,
			[CallerLineNumber] int line = 0,
			[CallerFilePath] string file = "",
			[CallerMemberName] string member = "")
		{
			GpuZone zone = this.context.BeginZone(name, color, line, file, member);
			commandBuffer.WriteTimestamp(this.queryHeap, zone.BeginQueryId);
			return zone;
		}

		/// <summary>
		/// Records the closing timestamp and emits the zone-end event. Call it where the zone ends
		/// in the command stream, never inside a render pass: <c>WriteTimestamp</c> expands to
		/// <c>EndQuery</c> plus <c>ResolveQueryData</c>, and resolving inside a render pass is not
		/// allowed.
		/// </summary>
		public void EndZone(CommandBuffer commandBuffer, GpuZone zone)
		{
			commandBuffer.WriteTimestamp(this.queryHeap, zone.EndQueryId);
			zone.End();
			this.pending.Add((zone.BeginQueryId, zone.EndQueryId));
		}

		/// <summary>
		/// Delivers the timestamps of the zones recorded in previous frames. Call it at the top of
		/// the frame: what makes the data safe to read is the <c>WaitIdle</c> at the end of the
		/// previous frame, which guarantees the resolve commands have executed. Reading a frame
		/// late also keeps the map/copy off the critical path of the frame that produced it.
		/// </summary>
		public void Drain()
		{
			if (this.pending.Count == 0)
			{
				return;
			}

			// The whole heap in one map: 512 bytes, cheaper than reasoning about the two ids of a
			// zone straddling the ring wrap, where they are not adjacent. Slots that were never
			// written hold garbage, and are never submitted — only tracked ids are.
			if (!this.queryHeap.ReadData(0, Capacity, this.results))
			{
				// Not ready. Keep the pairs and retry next frame rather than drop them: a zone
				// whose timestamps never arrive stays open in the capture forever. Evergine's
				// DirectX 12 backend always reports success, so this branch is insurance for
				// other backends, not something this sample exercises.
				return;
			}

			// The first pair is the outermost zone of the frame, hence the frame's GPU time.
			(ushort Begin, ushort End) outer = this.pending[0];
			this.LastFrameMilliseconds = (this.results[outer.End] - this.results[outer.Begin]) * this.millisecondsPerTick;
			this.lastRawTimestamp = (long)this.results[outer.Begin];

			foreach ((ushort begin, ushort end) in this.pending)
			{
				this.context.SubmitTime(begin, (long)this.results[begin]);
				this.context.SubmitTime(end, (long)this.results[end]);
			}

			this.pending.Clear();

			// Uncalibrated context: without a periodic anchor the GPU track slides away from the
			// CPU timeline. The timestamp reused here is a frame old, which is nothing against the
			// drift it corrects — the same trade upstream's OpenGL helper makes.
			if (++this.framesSinceSync >= SyncEveryFrames)
			{
				this.framesSinceSync = 0;
				this.context.TimeSync(this.lastRawTimestamp);
			}
		}

		public void Dispose()
		{
			this.queryHeap?.Dispose();
		}
	}
}
