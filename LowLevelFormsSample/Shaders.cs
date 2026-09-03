namespace LowLevelFormsSample
{
	/// <summary>
	/// The scene is deliberately as cheap as a scene can be while still being a real draw call:
	/// one constant buffer slot per cube and a Lambert term. The sample measures what it costs to
	/// <em>record</em> those draws, so anything expensive in the shader would only add noise to
	/// the GPU track and none of it to the CPU zone under study.
	/// </summary>
	internal static class Shaders
	{
		public const string Hlsl = """
			cbuffer PerObject : register(b0)
			{
				float4x4 worldViewProj;
				float4x4 world;
				float4 color;
			};

			struct VS_IN
			{
				float3 pos : POSITION;
				float3 nor : NORMAL;
			};

			struct PS_IN
			{
				float4 pos : SV_POSITION;
				float3 nor : NORMAL;
				float4 col : COLOR;
			};

			PS_IN VS(VS_IN input)
			{
				PS_IN output = (PS_IN)0;
				output.pos = mul(float4(input.pos, 1.0), worldViewProj);
				output.nor = normalize(mul(float4(input.nor, 0.0), world).xyz);
				output.col = color;
				return output;
			}

			float4 PS(PS_IN input) : SV_Target
			{
				float3 lightDir = normalize(float3(-0.35, 0.82, -0.45));
				float ndl = saturate(dot(normalize(input.nor), lightDir));
				float lighting = 0.35 + 0.65 * ndl;
				return float4(input.col.rgb * lighting, input.col.a);
			}
			""";
	}
}
