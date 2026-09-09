using Evergine.Common.Graphics.VertexFormats;
using Evergine.Mathematics;

namespace LowLevelFormsSample
{
	/// <summary>
	/// A unit cube built in code: 24 vertices (four per face, so each face carries its own flat
	/// normal) and 36 indices. Every cube in the scene reuses this one pair of buffers, so what
	/// varies per draw is the constant buffer offset, which is exactly the per-draw-call cost
	/// this sample is measuring.
	/// </summary>
	internal static class CubeMesh
	{
		public static void Build(out VertexPositionNormal[] vertices, out ushort[] indices)
		{
			var faces = new[]
			{
				// normal, tangent (u axis), bitangent (v axis)
				(Normal: Vector3.UnitZ, U: Vector3.UnitX, V: Vector3.UnitY),
				(Normal: -Vector3.UnitZ, U: -Vector3.UnitX, V: Vector3.UnitY),
				(Normal: Vector3.UnitX, U: -Vector3.UnitZ, V: Vector3.UnitY),
				(Normal: -Vector3.UnitX, U: Vector3.UnitZ, V: Vector3.UnitY),
				(Normal: Vector3.UnitY, U: Vector3.UnitX, V: -Vector3.UnitZ),
				(Normal: -Vector3.UnitY, U: Vector3.UnitX, V: Vector3.UnitZ),
			};

			vertices = new VertexPositionNormal[faces.Length * 4];
			indices = new ushort[faces.Length * 6];

			for (int face = 0; face < faces.Length; face++)
			{
				var (normal, u, v) = faces[face];
				Vector3 center = normal * 0.5f;

				int baseVertex = face * 4;
				vertices[baseVertex + 0] = new VertexPositionNormal(center - (u * 0.5f) - (v * 0.5f), normal);
				vertices[baseVertex + 1] = new VertexPositionNormal(center - (u * 0.5f) + (v * 0.5f), normal);
				vertices[baseVertex + 2] = new VertexPositionNormal(center + (u * 0.5f) + (v * 0.5f), normal);
				vertices[baseVertex + 3] = new VertexPositionNormal(center + (u * 0.5f) - (v * 0.5f), normal);

				int baseIndex = face * 6;
				indices[baseIndex + 0] = (ushort)(baseVertex + 0);
				indices[baseIndex + 1] = (ushort)(baseVertex + 1);
				indices[baseIndex + 2] = (ushort)(baseVertex + 2);
				indices[baseIndex + 3] = (ushort)(baseVertex + 0);
				indices[baseIndex + 4] = (ushort)(baseVertex + 2);
				indices[baseIndex + 5] = (ushort)(baseVertex + 3);
			}
		}
	}
}
