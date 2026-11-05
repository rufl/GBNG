Shader "UI/Procedural UI Image"
{
  Properties
  {
    [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
    _Color ("Tint", Color) = (1,1,1,1)
    _TextureSampleAdd ("Texture Sample Add", Vector) = (0,0,0,0)
    _StencilComp ("Stencil Comparison", Float) = 8
    _Stencil ("Stencil ID", Float) = 0
    _StencilOp ("Stencil Operation", Float) = 0
    _StencilWriteMask ("Stencil Write Mask", Float) = 255
    _StencilReadMask ("Stencil Read Mask", Float) = 255
    _ColorMask ("Color Mask", Float) = 15
    [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
  }

  SubShader
  {
    Tags
    {
      "Queue"="Transparent"
      "IgnoreProjector"="True"
      "RenderType"="Transparent"
      "PreviewType"="Plane"
      "CanUseSpriteAtlas"="True"
    }

    Stencil
    {
      Ref [_Stencil]
      Comp [_StencilComp]
      Pass [_StencilOp]
      ReadMask [_StencilReadMask]
      WriteMask [_StencilWriteMask]
    }

    Cull Off
    Lighting Off
    ZWrite Off
    ZTest [unity_GUIZTestMode]
    Blend SrcAlpha OneMinusSrcAlpha
    ColorMask [_ColorMask]

    Pass
    {
      Name "Default"
      CGPROGRAM
      #pragma target 3.0
      #pragma vertex vert
      #pragma fragment frag
      #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
      #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
      #pragma multi_compile_instancing

      #include "UnityCG.cginc"
      #include "UnityUI.cginc"

      sampler2D _MainTex;
      float4 _MainTex_ST;
      fixed4 _Color;
      fixed4 _TextureSampleAdd;
      float4 _ClipRect;

      struct appdata
      {
        float4 vertex : POSITION;
        fixed4 color : COLOR;
        float2 uv : TEXCOORD0;
        float3 normal : NORMAL;
        float2 size : TEXCOORD1;
        float2 packedRadii : TEXCOORD2;
        float2 borderAndPadding : TEXCOORD3;
        UNITY_VERTEX_INPUT_INSTANCE_ID
      };

      struct v2f
      {
        float4 vertex : SV_POSITION;
        fixed4 color : COLOR;
        float2 uv : TEXCOORD0;
        float2 localPosition : TEXCOORD1;
        float2 size : TEXCOORD2;
        float2 packedRadii : TEXCOORD3;
        float2 borderAndPadding : TEXCOORD4;
        float4 worldPosition : TEXCOORD5;
        UNITY_VERTEX_OUTPUT_STEREO
      };

      v2f vert(appdata v)
      {
        v2f o;
        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        o.worldPosition = v.vertex;
        o.localPosition = v.vertex.xy - v.normal.xy;
        o.vertex = UnityObjectToClipPos(v.vertex);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        o.color = v.color * _Color;
        o.size = v.size;
        o.packedRadii = v.packedRadii;
        o.borderAndPadding = v.borderAndPadding;
        return o;
      }

      float2 DecodePair(float packed)
      {
        const float denominator = 65535.0;
        float scaled = packed * denominator;
        float first = floor(scaled);
        float second = floor(frac(scaled) * denominator + 0.5);
        return float2(first, second) / 65534.0;
      }

      float RoundedRectangleDistance(float2 position, float2 size, float2 packedRadii, float padding)
      {
        float2 horizontalRadii = DecodePair(packedRadii.x);
        float2 verticalRadii = DecodePair(packedRadii.y);
        float topLeft = horizontalRadii.x;
        float topRight = horizontalRadii.y;
        float bottomRight = verticalRadii.x;
        float bottomLeft = verticalRadii.y;
        float radius = position.x < 0.0
          ? (position.y >= 0.0 ? topLeft : bottomLeft)
          : (position.y >= 0.0 ? topRight : bottomRight);

        radius *= min(size.x, size.y);
        float2 halfSize = (size + float2(padding, padding)) * 0.5;
        float2 q = abs(position) - (halfSize - radius);
        return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
      }

      fixed4 frag(v2f i) : SV_Target
      {
        float padding = max(0.0, i.borderAndPadding.y);
        float distanceToEdge = RoundedRectangleDistance(i.localPosition, i.size, i.packedRadii, padding);
        float antialias = max(fwidth(distanceToEdge), 0.0001);
        float outerCoverage = 1.0 - smoothstep(-antialias, antialias, distanceToEdge);

        float borderSetting = saturate(i.borderAndPadding.x);
        float coverage = outerCoverage;
        if (borderSetting < 0.9999)
        {
          float border = borderSetting * min(i.size.x, i.size.y) * 0.5;
          float innerCoverage = 1.0 - smoothstep(-border - antialias, -border + antialias, distanceToEdge);
          coverage = max(0.0, outerCoverage - innerCoverage);
        }

        fixed4 color = (tex2D(_MainTex, i.uv) + _TextureSampleAdd) * i.color;
        color.a *= coverage;

        #ifdef UNITY_UI_CLIP_RECT
        color.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
        #endif

        #ifdef UNITY_UI_ALPHACLIP
        clip(color.a - 0.001);
        #endif

        return color;
      }
      ENDCG
    }
  }
}
