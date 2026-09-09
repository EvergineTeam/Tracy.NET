using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Evergine.Bindings.Tracy;
using Evergine.Common.Graphics;
using Evergine.Common.Graphics.VertexFormats;
using Evergine.DirectX12;
using Evergine.Forms;
using Evergine.Vulkan;
using Buffer = Evergine.Common.Graphics.Buffer;
using Color = Evergine.Common.Graphics.Color;
using Matrix4x4 = Evergine.Mathematics.Matrix4x4;
using Rectangle = Evergine.Mathematics.Rectangle;
using Vector3 = Evergine.Mathematics.Vector3;
using Vector4 = Evergine.Mathematics.Vector4;

namespace LowLevelFormsSample
{
	/// <summary>
	/// A Windows Forms window drawing N cubes through a DirectX 12 or Vulkan swap chain on
	/// Evergine's low-level graphics API, with three frames in flight, instrumented with Tracy
	/// from end to end.
	///
	/// The question the sample answers is how long a frame spends <em>recording</em> its command
	/// buffer, which is why the cube count is a slider rather than a constant: dragging it changes
	/// nothing about the work per cube and everything about how many draw calls the frame issues,
	/// so the <c>RecordCommands</c> zone in the viewer has to move with it. If it does not, the
	/// capture is not measuring what it claims to.
	///
	/// The second question is whether the GPU track sits where it should against the CPU zones.
	/// With frames in flight and vsync off the GPU runs a frame or more behind the CPU, so a
	/// misaligned track is easy to mistake for a correct one; <see cref="GpuFrameProfiler"/>
	/// calibrates both clocks where the backend allows it, and <c>--no-calibration</c> shows what
	/// the uncalibrated scheme looks like on the same machine.
	///
	/// It is also the only place in this repository where the GPU layer meets a real GPU: the
	/// smoke test drives <see cref="GpuProfilerContext"/> with a synthetic clock, no query heap
	/// and no driver, which cannot catch a mistake in how timestamps are collected.
	/// </summary>
	internal static unsafe class Program
	{
		private const int RenderWidth = 1280;
		private const int RenderHeight = 720;

		/// <summary>
		/// Frames the CPU may record ahead of the GPU. Each has its own fence and its own copy of
		/// the per-frame constants: the GPU may still be reading frame N's buffer while the CPU
		/// records frame N+1, so sharing one would race.
		/// </summary>
		private const int FramesInFlight = 3;

		/// <summary>Per-cube constant slot. Dynamic offsets have to be 256-byte aligned.</summary>
		private const uint CbSlotSize = 256;

		/// <summary>
		/// Upper bound the constant buffer is sized for: 8192 * 256 B = 2 MiB. At that count the
		/// recording cost is unmistakable: milliseconds, not the microseconds a single draw
		/// would leave buried in the noise.
		/// </summary>
		private const int MaxCubes = 8192;

		private const int InitialCubes = 512;

		/// <summary>
		/// Milliseconds of command recording past which the frame counts as over budget. Every
		/// piece of reactive instrumentation below hangs off this one number: the
		/// <c>RecordCommands</c> zone turns red and a message lands in the viewer's log, both
		/// driven by dragging the slider past it.
		/// </summary>
		private const double RecordBudgetMs = 4.0;

		/// <summary>
		/// Cubes per arena region. Only affects how the frame's constants are carved up, so it
		/// is purely about what the memory window shows: one allocation per this many cubes,
		/// instead of a single block whose size is the only thing that ever moves.
		/// </summary>
		private const int ArenaChunkCubes = 512;

		/// <summary>
		/// Named once so the allocation and the discard cannot drift apart. Tracy identifies a
		/// pool by pointer, and two spellings would be two pools, one of which never gets freed.
		/// </summary>
		private const string ArenaPool = "frame-arena";

		private static MainForm form;
		private static Backend backend;
		private static bool allowCalibration;
		private static GraphicsContext graphics;
		private static SwapChain swapChain;
		private static FrameBuffer frameBuffer;
		private static CommandQueue commandQueue;
		private static GraphicsPipelineState pipeline;
		private static ResourceSet[] resourceSets;
		private static Buffer vertexBuffer;
		private static Buffer indexBuffer;
		private static Buffer[] constantBuffers;
		private static Fence[] frameFences;
		private static uint indexCount;
		private static GpuFrameProfiler gpuProfiler;

		/// <summary>
		/// The frame arena: one native block reserved for <see cref="MaxCubes"/> in
		/// <c>Load</c>, never reallocated. Each frame bumps <see cref="arenaOffset"/> through it
		/// and releases everything at once with <c>MemDiscard</c>, which is the only way a bump
		/// allocator can free, and the reason the reservation can stay a one-off. The command
		/// buffer copies it into the frame's constant buffer at record time, so reusing it the
		/// next frame is safe even with frames in flight.
		/// </summary>
		private static byte* arena;

		private static uint arenaOffset;

		/// <summary>Last value of <see cref="GC.GetTotalAllocatedBytes(bool)"/>, to plot the per-frame delta.</summary>
		private static long lastAllocatedBytes;
		private static Buffer[] vertexBuffers;
		private static uint[] dynamicOffsets;
		private static Viewport[] viewports;
		private static Rectangle[] scissors;
		private static Matrix4x4 projection;

		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static readonly Stopwatch SectionTimer = new Stopwatch();
		private static bool resizePending;
		private static float animationTime;
		private static long frameIndex;

		private static double recordMs;
		private static double blockedMs;
		private static double presentMs;
		private static double hudTimer;
		private static int hudFrames;

		/// <summary>
		/// Seconds since the last over-budget message. Without it a sustained overrun would emit
		/// one message per frame and bury the viewer's log, which is the fastest way to make a
		/// useful signal useless.
		/// </summary>
		private static double messageTimer;

		private enum Backend
		{
			DirectX12,
			Vulkan,
		}

		[STAThread]
		private static void Main(string[] args)
		{
			// --cubes N only sets where the slider starts. Dragging it is the interactive way to
			// watch RecordCommands move; passing a count is the repeatable way, so two runs can be
			// captured and compared without anyone touching the window. --backend picks the API
			// and --no-calibration the alignment scheme, for the same reason: one capture per
			// configuration, nothing touched in between.
			int initialCubes = ParseCubeArgument(args);
			backend = ParseBackendArgument(args);
			allowCalibration = Array.IndexOf(args, "--no-calibration") < 0;

			// Before any window exists, or the swap chain is sized in virtual pixels and Windows
			// upscales the result.
			Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			form = new MainForm(RenderWidth, RenderHeight, initialCubes, MaxCubes);

			// The HWND must exist before the swap chain is built, and it must be read *after* the
			// control has been parented: WinForms recreates a control's handle when it is added to
			// a container, which would leave the swap chain bound to a dead window.
			form.CreateControl();
			IntPtr renderHandle = form.RenderControl.Handle;

			// Both backends take the same Win32 surface; Vulkan builds a VkSurfaceKHR from the HWND.
			graphics = backend == Backend.Vulkan
				? new VKGraphicsContext()
				: new DX12GraphicsContext();

			// Off unless asked for: the debug layers validate every call and multiply the recording
			// cost the sample exists to measure. --validation turns them on to chase an actual bug.
			bool validation = Array.IndexOf(args, "--validation") >= 0;
			graphics.CreateDevice(validation ? new ValidationLayer(ValidationLayer.NotifyMethod.Trace) : null);

			var swapChainDescription = new SwapChainDescription()
			{
				Width = (uint)Math.Max(form.RenderControl.ClientSize.Width, 1),
				Height = (uint)Math.Max(form.RenderControl.ClientSize.Height, 1),
				SurfaceInfo = new SurfaceInfo(renderHandle, SurfaceInfo.SurfaceTypes.Forms),
				ColorTargetFormat = PixelFormat.R8G8B8A8_UNorm,
				ColorTargetFlags = TextureFlags.RenderTarget | TextureFlags.ShaderResource,
				DepthStencilTargetFormat = PixelFormat.D24_UNorm_S8_UInt,
				DepthStencilTargetFlags = TextureFlags.DepthStencil,
				SampleCount = TextureSampleCount.None,
				IsWindowed = true,
				RefreshRate = 60,
			};

			swapChain = graphics.CreateSwapChain(swapChainDescription);

			// Deliberately off. With vsync the frame is pinned to the refresh rate and every
			// change in recording cost is absorbed by the wait in Present, which is precisely the
			// signal this sample exists to show, and the GPU track's alignment is only put to the
			// test when the GPU is allowed to run as far behind the CPU as the fences let it.
			swapChain.VerticalSync = false;

			form.RenderControl.ClientSizeChanged += (s, e) => resizePending = true;

			// Drive the loop from our own form, so it ends when the user closes the window.
			var windowSystem = new FormsWindowsSystem { AutoRegisterWindow = false };
			windowSystem.RegisterLoopThreadControl(form);
			windowSystem.Run(Load, Draw);

			// Frames may still be in flight when the window closes; nothing below may be released
			// while the GPU can still touch it.
			commandQueue?.WaitIdle();
			gpuProfiler?.Dispose();

			if (frameFences != null)
			{
				foreach (Fence fence in frameFences)
				{
					fence.Dispose();
				}
			}

			if (arena != null)
			{
				// The one free this sample reports, matching the one allocation Load made for
				// the arena block itself.
				Profiler.MemFree((IntPtr)arena);
				NativeMemory.Free(arena);
				arena = null;
			}
		}

		private static int ParseCubeArgument(string[] args)
		{
			for (int i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == "--cubes" && int.TryParse(args[i + 1], out int cubes))
				{
					return Math.Clamp(cubes, 1, MaxCubes);
				}
			}

			return InitialCubes;
		}

		private static Backend ParseBackendArgument(string[] args)
		{
			for (int i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == "--backend")
				{
					switch (args[i + 1].ToLowerInvariant())
					{
						case "vulkan":
						case "vk":
							return Backend.Vulkan;
						case "dx12":
						case "directx12":
						case "d3d12":
							return Backend.DirectX12;
						default:
							throw new ArgumentException($"Unknown backend '{args[i + 1]}'. Use --backend dx12 or --backend vulkan.");
					}
				}
			}

			return Backend.DirectX12;
		}

		private static void Load()
		{
			// RenderLoop.Run drives the callback from the thread that called it, which is the STA
			// UI thread, so this is the thread the whole capture is about.
			Profiler.SetThreadName("render (UI)");
			Profiler.AppInfo($"Tracy.NET {backend} + Windows Forms sample");

			frameBuffer = swapChain.FrameBuffer;

			var vsBytes = ShaderCompiler.Compile(graphics, Shaders.Hlsl, "VS", ShaderStages.Vertex);
			var psBytes = ShaderCompiler.Compile(graphics, Shaders.Hlsl, "PS", ShaderStages.Pixel);
			var vsDescription = new ShaderDescription(ShaderStages.Vertex, "VS", vsBytes);
			var psDescription = new ShaderDescription(ShaderStages.Pixel, "PS", psBytes);
			var vertexShader = graphics.Factory.CreateShader(ref vsDescription);
			var pixelShader = graphics.Factory.CreateShader(ref psDescription);

			CubeMesh.Build(out VertexPositionNormal[] vertices, out ushort[] indices);
			indexCount = (uint)indices.Length;

			uint vertexBytes = (uint)(Marshal.SizeOf<VertexPositionNormal>() * vertices.Length);
			uint indexBytes = sizeof(ushort) * (uint)indices.Length;
			uint constantBytes = CbSlotSize * MaxCubes;

			var vbDescription = new BufferDescription(
				vertexBytes,
				BufferFlags.VertexBuffer,
				ResourceUsage.Immutable);
			var ibDescription = new BufferDescription(
				indexBytes,
				BufferFlags.IndexBuffer,
				ResourceUsage.Immutable);

			vertexBuffer = graphics.Factory.CreateBuffer(vertices, ref vbDescription);
			indexBuffer = graphics.Factory.CreateBuffer(indices, ref ibDescription);

			// One slot per cube, allocated once for the maximum: the slider must not be able to
			// trigger a reallocation mid-capture, or the zone it is supposed to explain would be
			// measuring buffer creation instead.
			arena = (byte*)NativeMemory.Alloc(constantBytes);

			// The arena's own reservation, in the default pool: one real native allocation,
			// freed once when the loop ends. The regions handed out of it every frame are a
			// different story and live in their own pool, see ArenaAlloc.
			Profiler.MemAlloc((IntPtr)arena, constantBytes);

			// What the application owns on the device, kept apart from CPU memory. These buffers
			// live until the process dies, so no free is ever reported and the viewer lists them
			// as still allocated at exit, which is the truth. Inventing a free to make the books
			// look tidy would end the capture, not clean it.
			const string gpuPool = "gpu";
			Profiler.MemAlloc(vertexBuffer.NativePointer, vertexBytes, gpuPool);
			Profiler.MemAlloc(indexBuffer.NativePointer, indexBytes, gpuPool);

			var layoutDescription = new ResourceLayoutDescription(
				new LayoutElementDescription(0, ResourceType.ConstantBuffer,
					ShaderStages.Vertex | ShaderStages.Pixel, allowDynamicOffset: true, size: CbSlotSize));
			var resourceLayout = graphics.Factory.CreateResourceLayout(ref layoutDescription);

			// One constant buffer and one resource set per frame in flight, see FramesInFlight.
			constantBuffers = new Buffer[FramesInFlight];
			resourceSets = new ResourceSet[FramesInFlight];
			frameFences = new Fence[FramesInFlight];
			for (int i = 0; i < FramesInFlight; i++)
			{
				var cbDescription = new BufferDescription(
					constantBytes, BufferFlags.ConstantBuffer, ResourceUsage.Default);
				constantBuffers[i] = graphics.Factory.CreateBuffer(ref cbDescription);
				constantBuffers[i].Name = $"Constants {i}";
				Profiler.MemAlloc(constantBuffers[i].NativePointer, constantBytes, gpuPool);

				var resourceSetDescription = new ResourceSetDescription(resourceLayout, constantBuffers[i]);
				resourceSets[i] = graphics.Factory.CreateResourceSet(ref resourceSetDescription);

				frameFences[i] = graphics.Factory.CreateFence();
				frameFences[i].Name = $"Frame fence {i}";
			}

			// Tracy's alloc/free model cannot see the managed heap, since .NET exposes no hook
			// for it, so the GC gets plots instead. Configured before the first sample, or the
			// viewer draws the byte counts as bare numbers.
			Profiler.PlotConfig("gc allocated", TracyPlotFormatEnum.TracyPlotFormatMemory, color: TracyColor.MediumPurple);
			Profiler.PlotConfig("gc gen0", TracyPlotFormatEnum.TracyPlotFormatNumber, step: true, color: TracyColor.Goldenrod);

			var pipelineDescription = new GraphicsPipelineDescription()
			{
				PrimitiveTopology = PrimitiveTopology.TriangleList,
				InputLayouts = new InputLayouts().Add(VertexPositionNormal.VertexFormat),
				ResourceLayouts = new[] { resourceLayout },
				Shaders = new GraphicsShaderStateDescription()
				{
					VertexShader = vertexShader,
					PixelShader = pixelShader,
				},
				RenderStates = new RenderStateDescription()
				{
					RasterizerState = RasterizerStates.CullBack,
					BlendState = BlendStates.Opaque,
					DepthStencilState = DepthStencilStates.ReadWrite,
				},
				Outputs = frameBuffer.OutputDescription,
			};

			pipeline = graphics.Factory.CreateGraphicsPipeline(ref pipelineDescription);
			commandQueue = graphics.Factory.CreateCommandQueue();

			var contextType = backend == Backend.Vulkan ? TracyGpuContextType.Vulkan : TracyGpuContextType.Direct3D12;
			gpuProfiler = GpuFrameProfiler.Create(graphics, commandQueue, $"{backend} frame", contextType, allowCalibration);
			form.SetBackend(backend.ToString(), gpuProfiler.IsCalibrated);

			Profiler.Message(
				gpuProfiler.IsCalibrated
					? $"{backend}: GPU track calibrated through CommandQueue.GetClockCalibration (startup anchor window was {gpuProfiler.AnchorErrorMilliseconds:F3} ms)"
					: $"{backend}: GPU track uncalibrated, anchored once inside a {gpuProfiler.AnchorErrorMilliseconds:F3} ms Submit/WaitIdle window",
				TracyMessageSeverity.TracyMessageSeverityInfo,
				gpuProfiler.IsCalibrated ? TracyColor.MediumSeaGreen : TracyColor.Goldenrod);

			vertexBuffers = new[] { vertexBuffer };
			dynamicOffsets = new uint[1];
			viewports = new Viewport[1];
			scissors = new Rectangle[1];
			ScreenSizeChanged(swapChain.SwapChainDescription.Width, swapChain.SwapChainDescription.Height);

			Clock.Restart();
		}

		private static void Draw()
		{
			double elapsed = Clock.Elapsed.TotalSeconds;
			Clock.Restart();

			int cubeCount = Math.Clamp(form.CubeCount, 1, MaxCubes);
			int slot = (int)(frameIndex % FramesInFlight);
			Fence frameFence = frameFences[slot];

			using (var frameZone = Profiler.BeginZone("Frame", TracyColor.SlateGray))
			{
				// Attached here on purpose: Tracy's zone events form a stack, so text and value
				// only reach this zone while it is still the innermost open one. After
				// FenceWait opens below, the very same calls would land on FenceWait.
				frameZone.Value((ulong)cubeCount);
				frameZone.Text($"{cubeCount} cubes, gpu {gpuProfiler.LastFrameMilliseconds:F2} ms last frame");

				// The fence of this slot was signaled by the frame that used it FramesInFlight
				// frames ago. Waiting on it is the only place the CPU blocks on the GPU: with
				// vsync off this is where a GPU-bound frame shows its cost.
				SectionTimer.Restart();
				using (Profiler.BeginZone("FenceWait", TracyColor.SteelBlue))
				{
					frameFence.Wait();
					frameFence.Reset();
				}

				blockedMs = SectionTimer.Elapsed.TotalMilliseconds;

				// Everything up to and including frame (frameIndex - FramesInFlight) is now done
				// on the GPU, so its timestamps can be read without stalling.
				using (Profiler.BeginZone("GpuReadback", TracyColor.MediumPurple))
				{
					gpuProfiler.Drain(frameIndex - FramesInFlight);
				}

				if (resizePending)
				{
					using (Profiler.BeginZone("Resize", TracyColor.Goldenrod))
					{
						ApplyPendingResize();
					}
				}

				swapChain.InitFrame();

				using (Profiler.BeginZone("Update", TracyColor.MediumSeaGreen))
				{
					if (!form.PauseButton.Checked)
					{
						animationTime += (float)elapsed;
					}

					FillConstants(cubeCount);
				}

				// The measurement the sample exists for: everything between here and End/Commit is
				// CPU work building the command list, with no GPU execution in it at all.
				SectionTimer.Restart();
				CommandBuffer commandBuffer;
				using (var recordZone = Profiler.BeginZone("RecordCommands", TracyColor.Orange))
				{
					commandBuffer = RecordCommands(cubeCount, slot);

					// The zones RecordCommands opened have all closed by now, so this one is
					// innermost again and the recolor lands on it. This is the difference the
					// two colors make: the one on BeginZone belongs to the call site and is
					// the same on every hit, this one belongs to the hit and says what it
					// measured. Drag the slider and the zone goes red before any number is read.
					recordZone.Color(SectionTimer.Elapsed.TotalMilliseconds > RecordBudgetMs
						? TracyColor.Crimson
						: TracyColor.Orange);
				}

				recordMs = SectionTimer.Elapsed.TotalMilliseconds;

				// The GPU zone recorded above starts executing no earlier than this call, which is
				// what the alignment check in the README compares the GPU track against.
				using (Profiler.BeginZone("Submit", TracyColor.Chocolate))
				{
					commandQueue.Submit(frameFence);
				}

				SectionTimer.Restart();
				using (Profiler.BeginZone("Present", TracyColor.CadetBlue))
				{
					swapChain.Present();
				}

				presentMs = SectionTimer.Elapsed.TotalMilliseconds;

				using (Profiler.BeginZone("Hud", TracyColor.DimGray))
				{
					UpdateHud(elapsed);
				}

				GC.KeepAlive(commandBuffer);
			}

			// Plots carry the same numbers the zones do, which is the cheapest cross-check there
			// is: a plot that disagrees with the width of its zone means the capture is wrong.
			Profiler.Plot("draw calls", cubeCount);
			Profiler.Plot("record ms", recordMs);
			Profiler.Plot("fence wait ms", blockedMs);
			Profiler.Plot("gpu ms", gpuProfiler.LastFrameMilliseconds);

			// Colored, and at most one a second. The message log is where you go to find the
			// moment something went wrong, so streaming a line per frame into it would cost
			// exactly the thing it is there to provide.
			messageTimer += elapsed;
			if (recordMs > RecordBudgetMs && messageTimer >= 1.0)
			{
				Profiler.Message(
					$"recording over budget: {recordMs:F2} ms for {cubeCount} cubes",
					TracyMessageSeverity.TracyMessageSeverityWarning,
					TracyColor.Crimson);

				messageTimer = 0;
			}

			// What Tracy cannot see on its own: the managed heap has no alloc/free hook, so it
			// gets plotted. The interpolated strings this sample hands to zone.Text and
			// zone.Name every frame are allocations, and this is where they surface.
			long allocated = GC.GetTotalAllocatedBytes(precise: false);
			Profiler.Plot("gc allocated", allocated - lastAllocatedBytes);
			Profiler.Plot("gc gen0", GC.CollectionCount(0));
			lastAllocatedBytes = allocated;

			// The frame's regions go all at once, which is the only kind of free a bump
			// allocator has. It also makes handing the same addresses out next frame legal:
			// without the discard that would read as allocating a live address twice, and Tracy
			// ends the session over it. Draw has no early return, so this always runs after the
			// allocations it releases. Keep it that way.
			arenaOffset = 0;
			Profiler.MemDiscard(ArenaPool);

			// Outside the Frame zone and after the present: this is where the frame actually ends.
			Profiler.FrameMark();
			frameIndex++;
		}

		/// <summary>
		/// Builds the command list for the frame. Every cube costs one dynamic-offset binding plus
		/// one indexed draw, the smallest honest unit of per-object recording work.
		/// </summary>
		private static CommandBuffer RecordCommands(int cubeCount, int slot)
		{
			Buffer constantBuffer = constantBuffers[slot];

			CommandBuffer commandBuffer = commandQueue.CommandBuffer();
			commandBuffer.Begin();

			// Outside the render pass on purpose: on DirectX 12 WriteTimestamp expands to EndQuery
			// plus ResolveQueryData, and resolving inside a render pass is not allowed.
			GpuZone gpuFrameZone = gpuProfiler.BeginZone(commandBuffer, "GPU frame", TracyColor.DarkTurquoise);

			using (Profiler.BeginZone("UploadConstants", TracyColor.Sienna))
			{
				// The arena's regions are contiguous and start at its base, so the whole frame
				// goes up in one call, and there is nothing to pin, since it was never managed.
				commandBuffer.UpdateBufferData(constantBuffer, (IntPtr)arena, (uint)cubeCount * CbSlotSize);

				commandBuffer.Barrier(new Buffer.Barrier(constantBuffer, Buffer.StateFlags.UniformBuffer));
			}

			commandBuffer.SetViewports(viewports);
			commandBuffer.SetScissorRectangles(scissors);

			var renderPassDescription = new RenderPassDescription(
				frameBuffer, new ClearValue(ClearFlags.All, new Color(28, 32, 44)));
			commandBuffer.BeginRenderPass(ref renderPassDescription);
			commandBuffer.SetGraphicsPipelineState(pipeline);
			commandBuffer.SetVertexBuffers(vertexBuffers);
			commandBuffer.SetIndexBuffer(indexBuffer);

			using (var drawZone = Profiler.BeginZone("DrawCalls", TracyColor.Tomato))
			{
				// Per-instance name, against the call-site name in BeginZone above. Tracy copies
				// it, so unlike the interned source-location name it can change every frame,
				// which is what puts the cube count on the zone in the timeline instead of
				// leaving it in a tooltip.
				drawZone.Name($"DrawCalls x{cubeCount}");

				for (int i = 0; i < cubeCount; i++)
				{
					dynamicOffsets[0] = (uint)i * CbSlotSize;
					commandBuffer.SetResourceSet(resourceSets[slot], 0, dynamicOffsets);
					commandBuffer.DrawIndexed(indexCount);
				}
			}

			commandBuffer.EndRenderPass();

			gpuProfiler.EndZone(commandBuffer, gpuFrameZone, frameIndex);

			commandBuffer.End();
			commandBuffer.Commit();

			return commandBuffer;
		}

		/// <summary>
		/// Lays the cubes out on a lattice and spins the whole thing. Pure CPU maths, kept in its
		/// own zone so it never gets confused with the cost of recording the draws it feeds.
		/// </summary>
		private static void FillConstants(int cubeCount)
		{
			int side = (int)Math.Ceiling(Math.Cbrt(cubeCount));
			float spacing = 1.9f;
			float extent = (side - 1) * spacing * 0.5f;

			// Frame the lattice whatever its size, so the slider changes the draw count and not
			// how much of the scene is on screen.
			float distance = Math.Max(extent * 2.6f, 4f);
			float orbit = animationTime * 0.25f;
			var eye = new Vector3(
				(float)Math.Sin(orbit) * distance,
				distance * 0.45f,
				(float)Math.Cos(orbit) * distance);

			Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
			Matrix4x4 viewProj = Matrix4x4.Multiply(view, projection);

			// Filled in chunks, each one its own arena region, so the slider moves the *number*
			// of live allocations in the viewer and not just the total byte count. The chunks
			// come out contiguous (a bump allocator reset to zero every frame), which is what
			// lets RecordCommands upload all of them with a single pointer.
			for (int chunkStart = 0; chunkStart < cubeCount; chunkStart += ArenaChunkCubes)
			{
				int chunkCubes = Math.Min(ArenaChunkCubes, cubeCount - chunkStart);
				byte* basePtr = ArenaAlloc((uint)chunkCubes * CbSlotSize);

				for (int i = chunkStart; i < chunkStart + chunkCubes; i++)
				{
					int x = i % side;
					int y = (i / side) % side;
					int z = i / (side * side);

					var position = new Vector3(
						(x * spacing) - extent,
						(y * spacing) - extent,
						(z * spacing) - extent);

					Matrix4x4 world =
						Matrix4x4.CreateFromYawPitchRoll(animationTime + (i * 0.07f), animationTime * 0.6f, 0f) *
						Matrix4x4.CreateTranslation(position);

					var slot = (PerObject*)(basePtr + ((i - chunkStart) * CbSlotSize));
					slot->WorldViewProj = Matrix4x4.Multiply(world, viewProj);
					slot->World = world;
					slot->Color = HueToColor(i * 0.11f);
				}
			}
		}

		/// <summary>
		/// Hands out the next region of the frame arena and reports it. No allocator runs here:
		/// the block came from <c>Load</c> and this is pointer arithmetic, which is exactly why
		/// the reporting has to be explicit: nothing else could see these regions.
		/// </summary>
		private static byte* ArenaAlloc(uint size)
		{
			byte* block = arena + arenaOffset;
			arenaOffset += size;

			Profiler.MemAlloc((IntPtr)block, size, ArenaPool);
			return block;
		}

		private static Vector4 HueToColor(float hue)
		{
			hue -= (float)Math.Floor(hue);
			float r = Math.Abs((hue * 6f) - 3f) - 1f;
			float g = 2f - Math.Abs((hue * 6f) - 2f);
			float b = 2f - Math.Abs((hue * 6f) - 4f);
			return new Vector4(
				Math.Clamp(r, 0f, 1f),
				Math.Clamp(g, 0f, 1f),
				Math.Clamp(b, 0f, 1f),
				1f);
		}

		private static void ApplyPendingResize()
		{
			resizePending = false;

			// The swap chain images may still be in use by the frames in flight; resizing them
			// under the GPU is the one thing the fences cannot protect against.
			commandQueue.WaitIdle();

			uint width = (uint)Math.Max(form.RenderControl.ClientSize.Width, 1);
			uint height = (uint)Math.Max(form.RenderControl.ClientSize.Height, 1);

			swapChain.ResizeSwapChain(width, height);

			// The frame buffer object is replaced by the resize; keeping the old one renders into
			// a target that no longer exists.
			frameBuffer = swapChain.FrameBuffer;

			ScreenSizeChanged(width, height);
		}

		private static void ScreenSizeChanged(uint width, uint height)
		{
			viewports[0] = new Viewport(0, 0, width, height);
			scissors[0] = new Rectangle(0, 0, (int)width, (int)height);
			projection = Matrix4x4.CreatePerspectiveFieldOfView(
				Evergine.Mathematics.MathHelper.PiOver4,
				(float)width / height,
				0.1f,
				2000f,
				reverseDepthBuffer: true);
		}

		/// <summary>
		/// Refreshes the status bar a few times a second. Touching WinForms controls every frame
		/// would put its own cost inside the capture, which is exactly the kind of contamination
		/// this sample is supposed to make visible rather than commit.
		/// </summary>
		private static void UpdateHud(double elapsed)
		{
			hudTimer += elapsed;
			hudFrames++;

			if (hudTimer < 0.25)
			{
				return;
			}

			double fps = hudFrames / hudTimer;
			form.SetTimings(recordMs, blockedMs, presentMs, gpuProfiler.LastFrameMilliseconds, fps);
			form.SetTracyConnected(Profiler.IsConnected);

			hudTimer = 0;
			hudFrames = 0;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PerObject
		{
			public Matrix4x4 WorldViewProj;
			public Matrix4x4 World;
			public Vector4 Color;
		}
	}
}
