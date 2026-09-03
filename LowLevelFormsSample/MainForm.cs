using System;
using System.Drawing;
using System.Windows.Forms;
using Evergine.Forms;

namespace LowLevelFormsSample
{
	/// <summary>
	/// A plain Windows Forms window hosting an <see cref="EvergineControl"/>: DirectX 12 renders
	/// into that control's HWND while the rest of the window stays ordinary WinForms. The two
	/// controls that matter for profiling are the cube-count slider — moving it changes how many
	/// draw calls the frame records, which is the whole point of the sample — and the status bar,
	/// which reports whether a Tracy viewer is attached without having to look at the viewer.
	/// </summary>
	internal sealed class MainForm : Form
	{
		private readonly TrackBar cubeSlider;
		private readonly ToolStripLabel cubeLabel;
		private readonly ToolStripStatusLabel timingsLabel;
		private readonly ToolStripStatusLabel tracyLabel;

		private volatile int cubeCount;

		public MainForm(int renderWidth, int renderHeight, int initialCubes, int maxCubes)
		{
			this.cubeCount = initialCubes;

			this.Text = "Tracy.NET - DirectX 12 command recording";
			this.StartPosition = FormStartPosition.CenterScreen;
			this.ClientSize = new Size(renderWidth, renderHeight + 60);
			this.MinimumSize = new Size(480, 360);
			this.DoubleBuffered = false;

			this.RenderControl = new EvergineControl
			{
				Dock = DockStyle.Fill,
			};

			// Logarithmic: the interesting range spans three orders of magnitude, and a linear
			// slider would spend nine tenths of its travel between 1000 and 8192 cubes, where
			// the recording cost per cube no longer changes.
			this.cubeSlider = new TrackBar
			{
				Minimum = 0,
				Maximum = SliderSteps,
				TickStyle = TickStyle.None,
				Width = 220,
				Value = CubesToSlider(initialCubes, maxCubes),
			};

			this.MaxCubes = maxCubes;
			this.cubeSlider.ValueChanged += this.OnCubeSliderChanged;

			this.cubeLabel = new ToolStripLabel(FormatCubeLabel(initialCubes))
			{
				AutoSize = false,
				Width = 110,
				TextAlign = ContentAlignment.MiddleLeft,
			};

			this.PauseButton = new ToolStripButton("Pause animation")
			{
				CheckOnClick = true,
				DisplayStyle = ToolStripItemDisplayStyle.Text,
			};

			var toolStrip = new ToolStrip
			{
				GripStyle = ToolStripGripStyle.Hidden,
				RenderMode = ToolStripRenderMode.System,
			};

			toolStrip.Items.Add(new ToolStripLabel("Cubes:"));
			toolStrip.Items.Add(new ToolStripControlHost(this.cubeSlider));
			toolStrip.Items.Add(this.cubeLabel);
			toolStrip.Items.Add(new ToolStripSeparator());
			toolStrip.Items.Add(this.PauseButton);

			this.timingsLabel = new ToolStripStatusLabel("Starting...")
			{
				Spring = true,
				TextAlign = ContentAlignment.MiddleLeft,
			};

			this.tracyLabel = new ToolStripStatusLabel("Tracy: waiting");

			var statusStrip = new StatusStrip();
			statusStrip.Items.Add(this.timingsLabel);
			statusStrip.Items.Add(this.tracyLabel);

			// Fill last so the docked render control gets whatever area the strips leave.
			this.Controls.Add(this.RenderControl);
			this.Controls.Add(toolStrip);
			this.Controls.Add(statusStrip);
		}

		/// <summary>Gets the control DirectX 12 presents into.</summary>
		public EvergineControl RenderControl { get; }

		/// <summary>Gets the toggle that freezes the animation without stopping the render loop.</summary>
		public ToolStripButton PauseButton { get; }

		/// <summary>Gets the upper bound the constant buffer was sized for.</summary>
		public int MaxCubes { get; }

		/// <summary>
		/// Gets how many cubes the frame should draw. Written from the WinForms event and read
		/// once per frame from the render loop, hence the volatile field behind it.
		/// </summary>
		public int CubeCount => this.cubeCount;

		/// <summary>Number of discrete positions on the logarithmic slider.</summary>
		private const int SliderSteps = 100;

		public void SetTimings(double recordMs, double submitMs, double presentMs, double gpuMs, double fps)
		{
			this.timingsLabel.Text = string.Create(
				System.Globalization.CultureInfo.InvariantCulture,
				$"record {recordMs,7:F3} ms   submit {submitMs,6:F2} ms   present {presentMs,5:F2} ms   gpu {gpuMs,6:F2} ms   |   {fps,5:F1} fps");
		}

		public void SetTracyConnected(bool connected)
		{
			this.tracyLabel.Text = connected ? "Tracy: connected" : "Tracy: waiting";
		}

		private void OnCubeSliderChanged(object sender, EventArgs e)
		{
			int cubes = SliderToCubes(this.cubeSlider.Value, this.MaxCubes);
			this.cubeCount = cubes;
			this.cubeLabel.Text = FormatCubeLabel(cubes);
		}

		private static string FormatCubeLabel(int cubes)
		{
			return $"{cubes:N0} draws";
		}

		private static int SliderToCubes(int value, int maxCubes)
		{
			double t = (double)value / SliderSteps;
			int cubes = (int)Math.Round(Math.Pow(maxCubes, t));
			return Math.Clamp(cubes, 1, maxCubes);
		}

		private static int CubesToSlider(int cubes, int maxCubes)
		{
			double t = Math.Log(Math.Clamp(cubes, 1, maxCubes)) / Math.Log(maxCubes);
			return Math.Clamp((int)Math.Round(t * SliderSteps), 0, SliderSteps);
		}
	}
}
