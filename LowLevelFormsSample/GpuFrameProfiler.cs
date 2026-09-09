using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Evergine.Bindings.Tracy;
using Evergine.Common.Graphics;

namespace LowLevelFormsSample
{
	/// <summary>
	/// Puts real GPU timestamps behind <see cref="GpuProfilerContext"/>. The binding owns the
	/// emission protocol and deliberately owns nothing else, neither query pool nor readback nor
	/// driver, so this class is the other half of the contract, written against Evergine's
	/// low-level graphics API exactly as the repository README describes it.
	///
	/// The whole design rests on one identity: <see cref="GpuProfilerContext"/> hands out query
	/// ids as plain ring indices modulo its capacity, so a <see cref="QueryHeap"/> created with
	/// the same capacity maps slot to id with no translation at all.
	///
	/// Alignment with the CPU timeline is the other half of the job. Where the backend exposes
	/// <see cref="CommandQueue.GetClockCalibration"/> the Tracy context is created from a
	/// simultaneous GPU/CPU pair and re-anchored on a fresh one every few frames; the viewer
	/// then maps GPU timestamps through the measured ratio of both clocks and the tracks line
	/// up regardless of how far the GPU runs behind the CPU. Without it the context is anchored
	/// once, on a timestamp the GPU executed somewhere inside a Submit/WaitIdle window whose
	/// width bounds the error, and is never re-anchored: the only GPU timestamps available
	/// later are read back frames after they executed, and handing one of those to TimeSync,
	/// which stamps the CPU side as "now", shifts the whole track by the age of that frame.
	/// </summary>
	internal sealed class GpuFrameProfiler : IDisposable
	{
		/// <summary>
		/// Shared by the Tracy context and the query heap, which is what makes ids and heap slots
		/// the same number. Two ids per zone, one zone per frame, three frames in flight: six live
		/// slots against sixty-four. Even, so a zone's begin and end are always adjacent slots
		/// and one two-slot read covers both.
		/// </summary>
		private const ushort Capacity = 64;

		/// <summary>
		/// The slot the startup anchor is written to. It is the first id the ring will hand out,
		/// which is harmless: the anchor is read exactly once, before any zone exists.
		/// </summary>
		private const uint AnchorSlot = 0;

		/// <summary>
		/// Frames between two calibration pairs. The drift being corrected is parts per million;
		/// once a second is plenty and keeps the calibration call out of the per-frame cost.
		/// </summary>
		private const int CalibrateEveryFrames = 60;

		private readonly QueryHeap queryHeap;
		private readonly GpuProfilerContext context;
		private readonly ClockCalibrator calibrator;
		private readonly ulong[] results = new ulong[Capacity];
		private readonly Queue<PendingZone> pending = new();
		private readonly double millisecondsPerTick;

		private int framesSinceCalibration;

		private GpuFrameProfiler(QueryHeap queryHeap, GpuProfilerContext context, ClockCalibrator calibrator, double millisecondsPerTick, double anchorErrorMilliseconds)
		{
			this.queryHeap = queryHeap;
			this.context = context;
			this.calibrator = calibrator;
			this.millisecondsPerTick = millisecondsPerTick;
			this.AnchorErrorMilliseconds = anchorErrorMilliseconds;
		}

		/// <summary>
		/// Gets the wall time of the outermost GPU zone of the last drained frame, in milliseconds.
		/// Purely for the on-screen status bar: it is the same span the viewer draws, which makes
		/// disagreement between the two easy to spot.
		/// </summary>
		public double LastFrameMilliseconds { get; private set; }

		/// <summary>Gets a value indicating whether the GPU track is kept aligned through clock calibration.</summary>
		public bool IsCalibrated => this.calibrator != null;

		/// <summary>
		/// Gets the width of the Submit/WaitIdle window the startup anchor was taken in. For an
		/// uncalibrated context that is the bound on how late the whole GPU track can sit; for a
		/// calibrated one it is only reported for comparison.
		/// </summary>
		public double AnchorErrorMilliseconds { get; }

		/// <summary>
		/// Creates the query heap and the Tracy context. Establishing the context needs one raw
		/// GPU timestamp, so this submits a command buffer that does nothing but write one and
		/// waits for it, a one-off cost at startup, before any frame exists. When the backend
		/// can calibrate, that timestamp is only a fallback and the context is anchored on a
		/// calibration pair instead, taken immediately before creating it.
		/// </summary>
		/// <param name="allowCalibration">False forces the uncalibrated scheme even where the backend supports calibration, to compare both in the viewer.</param>
		public static GpuFrameProfiler Create(GraphicsContext graphicsContext, CommandQueue commandQueue, string name, TracyGpuContextType type, bool allowCalibration)
		{
			var heapDescription = new QueryHeapDescription()
			{
				Type = QueryType.Timestamp,
				QueryCount = Capacity,
			};

			var queryHeap = graphicsContext.Factory.CreateQueryHeap(ref heapDescription);

			var commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();
			commandBuffer.WriteTimestamp(queryHeap, AnchorSlot);
			commandBuffer.End();
			commandBuffer.Commit();

			// The GPU executes the timestamp somewhere between these two readings.
			long cpuBefore = Stopwatch.GetTimestamp();
			commandQueue.Submit();
			commandQueue.WaitIdle();
			long cpuAfter = Stopwatch.GetTimestamp();

			var anchor = new ulong[Capacity];
			queryHeap.ReadData(AnchorSlot, 1, anchor);
			double anchorErrorMs = (cpuAfter - cpuBefore) * 1000.0 / Stopwatch.Frequency;

			// Ticks to nanoseconds, which is the unit Tracy's period is expressed in.
			float periodNs = 1e9f / graphicsContext.TimestampFrequency;

			GpuProfilerContext context;
			ClockCalibrator calibrator = null;

			if (allowCalibration
				&& graphicsContext.Capabilities.IsClockCalibrationSupported
				&& commandQueue.GetClockCalibration(out ulong gpuNow, out long cpuNow))
			{
				// Nothing may run between the sample and Create: Tracy stamps the CPU half itself,
				// as "now", so the GPU half has to be as current as the call.
				context = GpuProfilerContext.Create(name, type, (long)gpuNow, periodNs, Capacity, calibrated: true);
				calibrator = new ClockCalibrator(commandQueue, cpuNow);
			}
			else
			{
				// Tracy stamps "now", which is cpuAfter; the timestamp executed no earlier than
				// cpuBefore, so the track sits late by at most the window between the two.
				context = GpuProfilerContext.Create(name, type, (long)anchor[AnchorSlot], periodNs, Capacity);
			}

			return new GpuFrameProfiler(queryHeap, context, calibrator, periodNs / 1e6, anchorErrorMs);
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
		/// in the command stream, never inside a render pass: on DirectX 12 <c>WriteTimestamp</c>
		/// expands to <c>EndQuery</c> plus <c>ResolveQueryData</c>, and resolving inside a render
		/// pass is not allowed. The frame index is what <see cref="Drain"/> later compares against
		/// the fences to know the timestamps have landed.
		/// </summary>
		public void EndZone(CommandBuffer commandBuffer, GpuZone zone, long frameIndex)
		{
			commandBuffer.WriteTimestamp(this.queryHeap, zone.EndQueryId);
			zone.End();
			this.pending.Enqueue(new PendingZone(frameIndex, zone.BeginQueryId, zone.EndQueryId));
		}

		/// <summary>
		/// Delivers the timestamps of every zone recorded in a frame the GPU is known to have
		/// finished, then re-anchors the clocks when due. Call it right after waiting on the
		/// frame's fence: the fence, not the return value of <c>ReadData</c>, is what makes the
		/// data safe to read: DirectX 12 reports success unconditionally.
		/// </summary>
		/// <param name="completedThroughFrame">The newest frame index whose fence has been waited on.</param>
		public void Drain(long completedThroughFrame)
		{
			bool first = true;

			while (this.pending.Count > 0 && this.pending.Peek().FrameIndex <= completedThroughFrame)
			{
				PendingZone zone = this.pending.Peek();

				// One read for both edges: begin is even and end is begin + 1, so they are adjacent
				// slots, and every backend writes query i at results[i]. Vulkan resets the slots as
				// it reads them, which is exactly what lets the ring reuse them; it also refuses a
				// read whose queries have not landed, which the fence rules out. The check is
				// insurance, and the zone is kept for another try rather than dropped: a zone
				// whose timestamps never arrive stays open in the capture forever.
				if (!this.queryHeap.ReadData(zone.BeginQueryId, 2, this.results))
				{
					break;
				}

				ulong begin = this.results[zone.BeginQueryId];
				ulong end = this.results[zone.EndQueryId];
				this.pending.Dequeue();

				this.context.SubmitTime(zone.BeginQueryId, (long)begin);
				this.context.SubmitTime(zone.EndQueryId, (long)end);

				if (first)
				{
					// The first zone of a frame is its outermost one, hence the frame's GPU time.
					this.LastFrameMilliseconds = (end - begin) * this.millisecondsPerTick;
					first = false;
				}
			}

			if (this.calibrator != null && ++this.framesSinceCalibration >= CalibrateEveryFrames)
			{
				this.framesSinceCalibration = 0;

				// Sample, then emit, with nothing in between: Calibrate stamps the CPU side as "now".
				if (this.calibrator.TrySample(out long gpuTimestamp, out long cpuDeltaNs))
				{
					this.context.Calibrate(gpuTimestamp, cpuDeltaNs);
				}
			}
		}

		public void Dispose()
		{
			this.queryHeap?.Dispose();
		}

		private readonly struct PendingZone
		{
			public readonly long FrameIndex;
			public readonly ushort BeginQueryId;
			public readonly ushort EndQueryId;

			public PendingZone(long frameIndex, ushort beginQueryId, ushort endQueryId)
			{
				this.FrameIndex = frameIndex;
				this.BeginQueryId = beginQueryId;
				this.EndQueryId = endQueryId;
			}
		}
	}
}
