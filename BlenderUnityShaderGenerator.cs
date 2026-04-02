using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Translates a Blender shader node graph (BubNodeGraphData) into a URP-compatible
/// HLSL .shader file and returns the compiled Shader asset.
/// </summary>
public static class BlenderUnityShaderGenerator
{
    private const string GeneratedShaderDir = "Assets/Shaders/BUB_Generated";
    private const string TextureImportDir   = "Assets/Textures/BUB_Live";

    // Cache: assetPath → last HLSL source written. Avoids recompile when source unchanged.
    private static readonly Dictionary<string, string> s_shaderSourceCache = new();

    // ── Public API ──────────────────────────────────────────────────────────

    public static Shader GetOrGenerateShader(BubNodeGraphData graph, string materialName)
    {
        string safeName  = SafeName(materialName);
        string shaderName = $"BUB/Generated_{safeName}";
        string assetPath  = $"{GeneratedShaderDir}/MML_{safeName}.shader";

        EnsureDir(GeneratedShaderDir);
        ImportReferencedTextures(graph);   // auto-copy textures from Blender paths

        string hlsl     = BuildShaderSource(graph, shaderName);
        string fullPath = Path.Combine(Application.dataPath,
            assetPath.Substring("Assets/".Length));

        // ★ Skip expensive recompile if shader source is identical to last write
        if (!s_shaderSourceCache.TryGetValue(assetPath, out string cachedHlsl) || cachedHlsl != hlsl)
        {
            File.WriteAllText(fullPath, hlsl, Encoding.UTF8);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            s_shaderSourceCache[assetPath] = hlsl;
            Debug.Log($"[BUB] Shader recompiled: {shaderName}");
        }

        return AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
    }

    /// <summary>
    /// Copies textures referenced by Blender img_path into Assets/Textures/BUB_Live/
    /// and imports them so they are available to the material.
    /// </summary>
    public static void ImportReferencedTextures(BubNodeGraphData graph)
    {
        if (graph?.nodes == null) return;
        EnsureDir(TextureImportDir);

        foreach (var node in graph.nodes)
        {
            if (node.type != "TEX_IMAGE") continue;
            if (string.IsNullOrEmpty(node.img_path) || !File.Exists(node.img_path)) continue;

            string fileName   = Path.GetFileName(node.img_path);
            string destAsset  = $"{TextureImportDir}/{fileName}";
            string destFull   = Path.Combine(Application.dataPath,
                                    destAsset.Substring("Assets/".Length));

            // Copy only if source is newer or destination doesn't exist
            bool needCopy = !File.Exists(destFull) ||
                            File.GetLastWriteTimeUtc(node.img_path) > File.GetLastWriteTimeUtc(destFull);
            if (needCopy)
            {
                File.Copy(node.img_path, destFull, overwrite: true);
                AssetDatabase.ImportAsset(destAsset, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[BUB] Imported texture: {fileName}");
            }

            // Ensure all sync textures are imported as linear (non-sRGB) Default type
            // so that our manual rgb*2-1 unpack in the shader works correctly
            var texImporter = AssetImporter.GetAtPath(destAsset) as TextureImporter;
            if (texImporter != null && texImporter.sRGBTexture)
            {
                texImporter.sRGBTexture = false;
                texImporter.SaveAndReimport();
            }
        }
    }

    /// <summary>Assigns texture properties after the material is created.</summary>
    public static void AssignTextureProperties(Material mat, BubNodeGraphData graph)
    {
        if (graph?.nodes == null) return;
        foreach (var node in graph.nodes)
        {
            if (node.type != "TEX_IMAGE" || string.IsNullOrEmpty(node.img_name)) continue;
            string propName = "_Tex_" + SafeName(node.name);
            if (!mat.HasProperty(propName)) continue;

            // Try img_path filename first (most reliable), then img_name
            string[] candidates = string.IsNullOrEmpty(node.img_path)
                ? new[] { node.img_name }
                : new[] { Path.GetFileName(node.img_path), node.img_name };

            Texture2D tex = null;
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                string texPath = $"{TextureImportDir}/{candidate}";
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex != null) break;
                foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".tga", ".exr" })
                {
                    tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath + ext);
                    if (tex != null) break;
                }
                if (tex != null) break;
            }
            if (tex != null) mat.SetTexture(propName, tex);
        }
    }

    // ── Core Builder ────────────────────────────────────────────────────────

    private static string BuildShaderSource(BubNodeGraphData graph, string shaderName)
    {
        if (graph?.nodes == null || graph.nodes.Length == 0)
            return FallbackShader(shaderName);

        // Index structures
        var nodeMap  = new Dictionary<string, BubNodeData>();
        foreach (var n in graph.nodes) nodeMap[n.name] = n;

        // linkMap[(toNode, toSocket)] = (fromNode, fromSocket)
        var linkMap  = new Dictionary<(string, string), (string, string)>();
        if (graph.links != null)
            foreach (var l in graph.links)
                linkMap[(l.tn, l.ts)] = (l.fn, l.fs);

        // Find Principled BSDF (or Emission shader)
        BubNodeData bsdf     = null;
        BubNodeData emission = null;
        foreach (var n in graph.nodes)
        {
            if (n.type == "BSDF_PRINCIPLED" && bsdf == null) bsdf = n;
            if (n.type == "EMISSION"         && emission == null) emission = n;
        }
        BubNodeData outputShaderNode = bsdf ?? emission;
        if (outputShaderNode == null) return FallbackShader(shaderName);

        // Topological sort (DFS post-order from the shader node's inputs)
        var visited = new HashSet<string>();
        var order   = new List<BubNodeData>();
        TopoVisit(outputShaderNode, nodeMap, linkMap, visited, order);

        // Collect image texture nodes → Properties + cbuffer entries
        var texNodes     = new List<BubNodeData>();
        var propLines    = new StringBuilder();
        var cbufferLines = new StringBuilder();
        var texDeclLines = new StringBuilder();

        foreach (var n in order)
        {
            if (n.type != "TEX_IMAGE") continue;
            string propName = "_Tex_" + SafeName(n.name);
            texNodes.Add(n);
            propLines.AppendLine($"        {propName} (\"Texture\", 2D) = \"white\" {{}}");
            texDeclLines.AppendLine($"            TEXTURE2D({propName});");
            texDeclLines.AppendLine($"            SAMPLER(sampler_{propName});");
        }

        // Generate per-node HLSL code
        var varTypes = new Dictionary<(string, string), string>(); // (node, socket) → hlsl type
        var sb       = new StringBuilder();

        foreach (var n in order)
        {
            if (n.type is "BSDF_PRINCIPLED" or "OUTPUT_MATERIAL" or "MIX_SHADER" or "EMISSION")
                continue; // handled specially at end
            EmitNode(n, nodeMap, linkMap, varTypes, sb);
        }

        // Resolve BSDF inputs (or emission inputs)
        string albedo    = "float3(0.8,0.8,0.8)";
        string metallic  = "0.0";
        string smoothness = "0.5";
        string emColor   = "float3(0,0,0)";
        string alpha     = "1.0";
        if (bsdf != null)
        {
            albedo     = GetInputF3(bsdf, "Base Color",       linkMap, varTypes, nodeMap, "float3(0.8,0.8,0.8)");
            metallic   = GetInputF (bsdf, "Metallic",         linkMap, varTypes, nodeMap, "0.0");
            string rgh = GetInputF (bsdf, "Roughness",        linkMap, varTypes, nodeMap, "0.5");
            smoothness = $"(1.0 - {rgh})";
            string emStr= GetInputF(bsdf, "Emission Strength", linkMap, varTypes, nodeMap, "0.0");
            string emClr= GetInputF3(bsdf,"Emission Color",   linkMap, varTypes, nodeMap,
                          GetInputF3(bsdf,"Emission",         linkMap, varTypes, nodeMap, "float3(0,0,0)"));
            emColor    = $"({emClr} * {emStr})";
            alpha      = GetInputF (bsdf, "Alpha",            linkMap, varTypes, nodeMap, "1.0");
        }
        else if (emission != null)
        {
            string emClr= GetInputF3(emission, "Color",    linkMap, varTypes, nodeMap, "float3(1,1,1)");
            string emStr= GetInputF (emission, "Strength", linkMap, varTypes, nodeMap, "1.0");
            emColor  = $"({emClr} * {emStr})";
            albedo   = "float3(0,0,0)";
        }

        // Resolve normal map — check if BSDF "Normal" input connects to a NORMAL_MAP node
        string normalTS = "float3(0,0,1)";
        if (bsdf != null && linkMap.TryGetValue((bsdf.name, "Normal"), out var normalSrc))
        {
            string nVar = GetOutputVar(normalSrc.Item1, normalSrc.Item2, varTypes);
            if (nVar != null) normalTS = nVar;
        }

        string renderType = "Opaque";
        string renderQueue = "Geometry";
        string blendLine  = "";
        // Use float comparison to avoid "1.0" vs "1.000000" string mismatch
        bool alphaIsOne = float.TryParse(alpha,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float alphaF)
            && alphaF >= 0.9999f;
        if (!alphaIsOne && alpha != "1.0")
        {
            renderType  = "Transparent";
            renderQueue = "Transparent";
            blendLine   = "Blend SrcAlpha OneMinusSrcAlpha\n            ZWrite Off";
        }

        return $@"// Auto-generated by MML Shader Generator — do not edit manually
Shader ""{shaderName}""
{{
    Properties
    {{
{propLines}    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""{renderType}"" ""RenderPipeline""=""UniversalPipeline"" ""Queue""=""{renderQueue}"" }}
        LOD 300
        {blendLine}

        Pass
        {{
            Name ""ForwardLit""
            Tags {{ ""LightMode"" = ""UniversalForward"" }}

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog
            #pragma target 3.5

            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl""

            CBUFFER_START(UnityPerMaterial)
{cbufferLines}            CBUFFER_END

{texDeclLines}
            struct Attributes
            {{
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            }};

            struct Varyings
            {{
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 tangentWS   : TEXCOORD1;
                float3 bitangentWS : TEXCOORD2;
                float2 uv          : TEXCOORD3;
                float3 positionWS  : TEXCOORD4;
                float3 viewDirWS   : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            }};

            Varyings vert(Attributes IN)
            {{
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   vni = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.positionHCS  = vpi.positionCS;
                OUT.positionWS   = vpi.positionWS;
                OUT.normalWS     = vni.normalWS;
                OUT.tangentWS    = vni.tangentWS;
                OUT.bitangentWS  = vni.bitangentWS;
                OUT.uv           = IN.uv;
                OUT.viewDirWS    = GetWorldSpaceViewDir(vpi.positionWS);
                return OUT;
            }}

            // ── Utility functions ───────────────────────────────────────────
            float mml_hash(float3 p)
            {{
                p = frac(p * float3(443.897,441.423,437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }}
            float mml_vnoise(float3 p)
            {{
                float3 i = floor(p); float3 f = frac(p);
                float3 u = f*f*(3.0-2.0*f);
                return lerp(lerp(lerp(mml_hash(i),             mml_hash(i+float3(1,0,0)),u.x),
                                 lerp(mml_hash(i+float3(0,1,0)),mml_hash(i+float3(1,1,0)),u.x),u.y),
                            lerp(lerp(mml_hash(i+float3(0,0,1)),mml_hash(i+float3(1,0,1)),u.x),
                                 lerp(mml_hash(i+float3(0,1,1)),mml_hash(i+float3(1,1,1)),u.x),u.y),u.z);
            }}
            float mml_fnoise(float3 p, float detail, float roughness)
            {{
                float v=0,amp=1,tot=0; int d=min(int(detail)+1,8);
                for(int i=0;i<d;i++){{ v+=amp*mml_vnoise(p); tot+=amp; amp*=roughness; p*=2.0; }}
                return tot>0 ? v/tot : 0;
            }}
            float3 mml_rgb2hsv(float3 c)
            {{
                float4 K=float4(0,-1.0/3,2.0/3,-1);
                float4 p=lerp(float4(c.bg,K.wz),float4(c.gb,K.xy),step(c.b,c.g));
                float4 q=lerp(float4(p.xyw,c.r),float4(c.r,p.yzx),step(p.x,c.r));
                float d=q.x-min(q.w,q.y); float e=1e-10;
                return float3(abs(q.z+(q.w-q.y)/(6*d+e)),d/(q.x+e),q.x);
            }}
            float3 mml_hsv2rgb(float3 c)
            {{
                float4 K=float4(1,2.0/3,1.0/3,3);
                float3 p=abs(frac(c.xxx+K.xyz)*6-K.www);
                return c.z*lerp(K.xxx,saturate(p-K.xxx),c.y);
            }}
            float mml_remap(float v,float fmin,float fmax,float tmin,float tmax)
            {{ return tmin+(v-fmin)/(fmax-fmin+1e-10)*(tmax-tmin); }}
            // ───────────────────────────────────────────────────────────────

            half4 frag(Varyings IN) : SV_Target
            {{
                float2 uv       = IN.uv;
                float3 normalWS = normalize(IN.normalWS);
                float3 viewDirWS= normalize(IN.viewDirWS);
                float3 posWS    = IN.positionWS;
                float3 posOS    = TransformWorldToObject(posWS);

                // ── Generated node code ─────────────────────────────────
{sb}                // ───────────────────────────────────────────────────────

                half3x3 tangentToWorld = half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                float3 finalNormalTS   = {normalTS};
                float3 finalNormalWS   = normalize(mul(finalNormalTS, tangentToWorld));

                InputData inputData = (InputData)0;
                inputData.positionWS       = IN.positionWS;
                inputData.normalWS         = finalNormalWS;
                inputData.viewDirectionWS  = viewDirWS;
                inputData.shadowCoord      = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord         = ComputeFogFactor(IN.positionHCS.z);
                inputData.tangentToWorld   = tangentToWorld;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = {albedo};
                surfaceData.metallic   = saturate({metallic});
                surfaceData.smoothness = saturate({smoothness});
                surfaceData.emission   = {emColor};
                surfaceData.alpha      = saturate({alpha});
                surfaceData.normalTS   = finalNormalTS;
                surfaceData.occlusion  = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }}
            ENDHLSL
        }}

        UsePass ""Universal Render Pipeline/Lit/ShadowCaster""
        UsePass ""Universal Render Pipeline/Lit/DepthOnly""
    }}
    Fallback Off
}}
";
    }

    // ── Topological Sort (DFS post-order) ───────────────────────────────────

    private static void TopoVisit(BubNodeData node,
        Dictionary<string, BubNodeData> nodeMap,
        Dictionary<(string, string), (string, string)> linkMap,
        HashSet<string> visited, List<BubNodeData> order)
    {
        if (visited.Contains(node.name)) return;
        visited.Add(node.name);
        // Visit all upstream nodes this node depends on
        if (node.inputs != null)
        {
            foreach (var inp in node.inputs)
            {
                if (linkMap.TryGetValue((node.name, inp.name), out var src))
                {
                    if (nodeMap.TryGetValue(src.Item1, out var srcNode))
                        TopoVisit(srcNode, nodeMap, linkMap, visited, order);
                }
            }
        }
        order.Add(node);
    }

    // ── Node Code Emission ──────────────────────────────────────────────────

    private static void EmitNode(BubNodeData node,
        Dictionary<string, BubNodeData> nodeMap,
        Dictionary<(string, string), (string, string)> linkMap,
        Dictionary<(string, string), string> varTypes,
        StringBuilder sb)
    {
        string v = "v_" + SafeVar(node.name); // variable name base

        switch (node.type)
        {
            // ── Constants ──────────────────────────────────────────────────
            case "RGB":
            {
                float[] c = InpDC(node, "Color");
                string val = c != null ? F3(c) : "float3(0.8,0.8,0.8)";
                sb.AppendLine($"                float3 {v}_Color = {val};");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }
            case "VALUE":
            {
                float d = InpDV(node, "Value");
                sb.AppendLine($"                float {v}_Value = {F(d)};");
                varTypes[(node.name, "Value")] = "float";
                break;
            }

            // ── Math ───────────────────────────────────────────────────────
            case "MATH":
            {
                string a = GetInputF(node, "Value",   linkMap, varTypes, nodeMap, "0.0");
                string b = GetInputF(node, "Value_1", linkMap, varTypes, nodeMap,
                           GetInputF(node, "Value",   linkMap, varTypes, nodeMap, "0.0"));
                // Blender numbers its Math inputs: Value, Value_001, etc.
                // JsonUtility gives them back as "Value", the second as index 1
                b = GetInputFByIndex(node, 1, linkMap, varTypes, nodeMap, "0.0");
                string c = GetInputFByIndex(node, 2, linkMap, varTypes, nodeMap, "0.0");
                string expr = MathExpr(node.op ?? "ADD", a, b, c);
                if (node.use_clamp) expr = $"saturate({expr})";
                sb.AppendLine($"                float {v}_Value = {expr};");
                varTypes[(node.name, "Value")] = "float";
                break;
            }

            // ── Mix RGB ────────────────────────────────────────────────────
            case "MIX_RGB":
            case "MIX":
            {
                // Blender 4.x MIX node can be float or color; check data_type
                bool isColor = (node.op == null || node.op != "FLOAT");
                if (isColor)
                {
                    string fac = GetInputF (node, "Fac",    linkMap, varTypes, nodeMap, "0.5");
                    string c1  = GetInputF3(node, "Color1", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                    string c2  = GetInputF3(node, "Color2", linkMap, varTypes, nodeMap, "float3(1,1,1)");
                    // Blender 4.x names
                    if (c1 == "float3(0,0,0)") c1 = GetInputF3(node, "A", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                    if (c2 == "float3(1,1,1)") c2 = GetInputF3(node, "B", linkMap, varTypes, nodeMap, "float3(1,1,1)");
                    string expr = BlendExpr(node.blend ?? "MIX", fac, c1, c2);
                    if (node.use_clamp) expr = $"saturate({expr})";
                    sb.AppendLine($"                float3 {v}_Color = {expr};");
                    varTypes[(node.name, "Color")] = "float3";
                    varTypes[(node.name, "Result")] = "float3";
                }
                else
                {
                    string fac = GetInputF(node, "Fac", linkMap, varTypes, nodeMap, "0.5");
                    string fa  = GetInputF(node, "A",   linkMap, varTypes, nodeMap, "0.0");
                    string fb  = GetInputF(node, "B",   linkMap, varTypes, nodeMap, "1.0");
                    sb.AppendLine($"                float {v}_Result = lerp({fa},{fb},{fac});");
                    varTypes[(node.name, "Result")] = "float";
                }
                break;
            }

            // ── Color Ramp ─────────────────────────────────────────────────
            case "VALTORGB":
            {
                string fac = GetInputF(node, "Fac", linkMap, varTypes, nodeMap, "0.5");
                string colorExpr = ColorRampExpr(node, fac, out string alphaExpr);
                sb.AppendLine($"                float3 {v}_Color = {colorExpr};");
                sb.AppendLine($"                float  {v}_Alpha = {alphaExpr};");
                varTypes[(node.name, "Color")] = "float3";
                varTypes[(node.name, "Alpha")]  = "float";
                break;
            }

            // ── Noise Texture ──────────────────────────────────────────────
            case "TEX_NOISE":
            {
                string vec   = GetInputF3(node, "Vector",     linkMap, varTypes, nodeMap, "float3(uv,0)");
                string scale = GetInputF (node, "Scale",      linkMap, varTypes, nodeMap, "5.0");
                string det   = GetInputF (node, "Detail",     linkMap, varTypes, nodeMap, "2.0");
                string rough = GetInputF (node, "Roughness",  linkMap, varTypes, nodeMap, "0.5");
                string dist  = GetInputF (node, "Distortion", linkMap, varTypes, nodeMap, "0.0");
                sb.AppendLine($"                float3 {v}_vec  = ({vec}) * {scale} + mml_vnoise(({vec})*7.3)*{dist};");
                sb.AppendLine($"                float  {v}_Fac  = mml_fnoise({v}_vec, {det}, {rough});");
                sb.AppendLine($"                float3 {v}_Color= float3({v}_Fac,{v}_Fac,{v}_Fac);");
                varTypes[(node.name, "Fac")]   = "float";
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Wave Texture ───────────────────────────────────────────────
            case "TEX_WAVE":
            {
                string vec   = GetInputF3(node, "Vector",           linkMap, varTypes, nodeMap, "float3(uv,0)");
                string scale = GetInputF (node, "Scale",            linkMap, varTypes, nodeMap, "5.0");
                string dist  = GetInputF (node, "Distortion",       linkMap, varTypes, nodeMap, "0.0");
                string det   = GetInputF (node, "Detail",           linkMap, varTypes, nodeMap, "2.0");
                string dscl  = GetInputF (node, "Detail Scale",     linkMap, varTypes, nodeMap, "1.0");
                string drgh  = GetInputF (node, "Detail Roughness", linkMap, varTypes, nodeMap, "0.5");
                string phase = GetInputF (node, "Phase Offset",     linkMap, varTypes, nodeMap, "0.0");
                bool bands = string.IsNullOrEmpty(node.op) || node.op == "BANDS";
                string coord = bands ? $"({vec}).x" : $"length({vec})";
                sb.AppendLine($"                float {v}_t = {coord} * {scale} + {phase};");
                sb.AppendLine($"                {v}_t += mml_fnoise(({vec})*{dscl},{det},{drgh}) * {dist};");
                bool sine = string.IsNullOrEmpty(node.cr_interp) || node.cr_interp == "SINE";
                string wave = sine ? $"(sin({v}_t * 6.2831853) * 0.5 + 0.5)"
                                   : $"abs(frac({v}_t * 0.5) * 2.0 - 1.0)";
                sb.AppendLine($"                float  {v}_Fac   = {wave};");
                sb.AppendLine($"                float3 {v}_Color = float3({v}_Fac,{v}_Fac,{v}_Fac);");
                varTypes[(node.name, "Fac")]   = "float";
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Gradient Texture ───────────────────────────────────────────
            case "TEX_GRADIENT":
            {
                string vec = GetInputF3(node, "Vector", linkMap, varTypes, nodeMap, "float3(uv,0)");
                string fac;
                switch (node.op ?? "LINEAR")
                {
                    case "QUADRATIC":       fac = $"(({vec}).x * ({vec}).x)"; break;
                    case "EASING":          fac = $"(smoothstep(0,1,({vec}).x))"; break;
                    case "DIAGONAL":        fac = $"(0.5*(({vec}).x + ({vec}).y))"; break;
                    case "SPHERICAL":       fac = $"saturate(1.0 - length({vec}))"; break;
                    case "QUADRATIC_SPHERE":fac = $"saturate(1.0 - dot({vec},{vec}))"; break;
                    case "RADIAL":          fac = $"(atan2(({vec}).y,({vec}).x)/6.2831853 + 0.5)"; break;
                    default:                fac = $"frac(({vec}).x)"; break;
                }
                sb.AppendLine($"                float  {v}_Fac   = {fac};");
                sb.AppendLine($"                float3 {v}_Color = float3({v}_Fac,{v}_Fac,{v}_Fac);");
                varTypes[(node.name, "Fac")]   = "float";
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Image Texture ──────────────────────────────────────────────
            case "TEX_IMAGE":
            {
                string propName = "_Tex_" + SafeVar(node.name);
                string vec = GetInputF3(node, "Vector", linkMap, varTypes, nodeMap, "");
                string uvCoord = string.IsNullOrEmpty(vec) ? "uv" : $"({vec}).xy";
                sb.AppendLine($"                float4 {v}_rgba  = SAMPLE_TEXTURE2D({propName}, sampler_{propName}, {uvCoord});");
                sb.AppendLine($"                float3 {v}_Color = {v}_rgba.rgb;");
                sb.AppendLine($"                float  {v}_Alpha = {v}_rgba.a;");
                varTypes[(node.name, "Color")] = "float3";
                varTypes[(node.name, "Alpha")]  = "float";
                break;
            }

            // ── Texture Coordinate ─────────────────────────────────────────
            case "TEXCOORD":
            case "TEX_COORD":
            {
                sb.AppendLine($"                float3 {v}_UV       = float3(uv, 0);");
                sb.AppendLine($"                float3 {v}_Normal   = normalWS;");
                sb.AppendLine($"                float3 {v}_Object   = posOS;");
                sb.AppendLine($"                float3 {v}_Generated= posOS * 0.5 + 0.5;");
                sb.AppendLine($"                float3 {v}_Window   = float3(uv, 0);");
                varTypes[(node.name, "UV")]        = "float3";
                varTypes[(node.name, "Normal")]    = "float3";
                varTypes[(node.name, "Object")]    = "float3";
                varTypes[(node.name, "Generated")] = "float3";
                varTypes[(node.name, "Window")]    = "float3";
                break;
            }

            // ── Mapping ────────────────────────────────────────────────────
            case "MAPPING":
            {
                string vec  = GetInputF3(node, "Vector",   linkMap, varTypes, nodeMap, "float3(uv,0)");
                string loc  = GetInputF3(node, "Location", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                string rot  = GetInputF3(node, "Rotation", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                string scl  = GetInputF3(node, "Scale",    linkMap, varTypes, nodeMap, "float3(1,1,1)");
                // Simplified: scale then offset (no rotation for now)
                sb.AppendLine($"                float3 {v}_Vector = ({vec} + {loc}) * {scl};");
                varTypes[(node.name, "Vector")] = "float3";
                break;
            }

            // ── Fresnel ────────────────────────────────────────────────────
            case "FRESNEL":
            {
                string ior = GetInputF(node, "IOR", linkMap, varTypes, nodeMap, "1.45");
                sb.AppendLine($"                float {v}_Fac = pow(1.0 - saturate(dot(normalWS, viewDirWS)), max(0.001, {ior}));");
                varTypes[(node.name, "Fac")] = "float";
                break;
            }

            // ── Layer Weight ───────────────────────────────────────────────
            case "LAYER_WEIGHT":
            {
                string blend = GetInputF(node, "Blend", linkMap, varTypes, nodeMap, "0.5");
                sb.AppendLine($"                float {v}_ndv  = saturate(dot(normalWS, viewDirWS));");
                sb.AppendLine($"                float {v}_Facing  = 1.0 - {v}_ndv;");
                sb.AppendLine($"                float {v}_Fresnel = pow(1.0 - {v}_ndv, max(0.001, {blend})*10.0);");
                varTypes[(node.name, "Facing")]  = "float";
                varTypes[(node.name, "Fresnel")] = "float";
                break;
            }

            // ── Invert ─────────────────────────────────────────────────────
            case "INVERT":
            {
                string fac   = GetInputF (node, "Fac",   linkMap, varTypes, nodeMap, "1.0");
                string color = GetInputF3(node, "Color", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                sb.AppendLine($"                float3 {v}_Color = lerp({color}, 1.0 - {color}, {fac});");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Clamp ──────────────────────────────────────────────────────
            case "CLAMP":
            {
                string val  = GetInputF(node, "Value", linkMap, varTypes, nodeMap, "0.0");
                string mn   = GetInputF(node, "Min",   linkMap, varTypes, nodeMap, "0.0");
                string mx   = GetInputF(node, "Max",   linkMap, varTypes, nodeMap, "1.0");
                sb.AppendLine($"                float {v}_Result = clamp({val},{mn},{mx});");
                varTypes[(node.name, "Result")] = "float";
                break;
            }

            // ── Map Range ──────────────────────────────────────────────────
            case "MAP_RANGE":
            {
                string val  = GetInputF(node, "Value",   linkMap, varTypes, nodeMap, "0.0");
                string fmin = GetInputF(node, "From Min",linkMap, varTypes, nodeMap, "0.0");
                string fmax = GetInputF(node, "From Max",linkMap, varTypes, nodeMap, "1.0");
                string tmin = GetInputF(node, "To Min",  linkMap, varTypes, nodeMap, "0.0");
                string tmax = GetInputF(node, "To Max",  linkMap, varTypes, nodeMap, "1.0");
                bool smooth = (node.op ?? "LINEAR") == "SMOOTHSTEP";
                string t = smooth
                    ? $"smoothstep(0,1,mml_remap({val},{fmin},{fmax},0,1))"
                    : $"mml_remap({val},{fmin},{fmax},{tmin},{tmax})";
                if (node.use_clamp) t = $"clamp({t},{tmin},{tmax})";
                sb.AppendLine($"                float {v}_Result = {t};");
                varTypes[(node.name, "Result")] = "float";
                break;
            }

            // ── Combine/Separate XYZ ───────────────────────────────────────
            case "COMBXYZ":
            case "COMBINE_XYZ":
            {
                string x = GetInputF(node, "X", linkMap, varTypes, nodeMap, "0.0");
                string y = GetInputF(node, "Y", linkMap, varTypes, nodeMap, "0.0");
                string z = GetInputF(node, "Z", linkMap, varTypes, nodeMap, "0.0");
                sb.AppendLine($"                float3 {v}_Vector = float3({x},{y},{z});");
                varTypes[(node.name, "Vector")] = "float3";
                break;
            }
            case "SEPXYZ":
            case "SEPARATE_XYZ":
            {
                string vec = GetInputF3(node, "Vector", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                sb.AppendLine($"                float3 {v}_tmp = {vec};");
                sb.AppendLine($"                float {v}_X = {v}_tmp.x;");
                sb.AppendLine($"                float {v}_Y = {v}_tmp.y;");
                sb.AppendLine($"                float {v}_Z = {v}_tmp.z;");
                varTypes[(node.name, "X")] = "float";
                varTypes[(node.name, "Y")] = "float";
                varTypes[(node.name, "Z")] = "float";
                break;
            }

            // ── Combine/Separate RGB (Blender 3.x) ────────────────────────
            case "COMBRGB":
            case "COMBINE_RGB":
            {
                string r = GetInputF(node, "R", linkMap, varTypes, nodeMap, "0.0");
                string g = GetInputF(node, "G", linkMap, varTypes, nodeMap, "0.0");
                string b = GetInputF(node, "B", linkMap, varTypes, nodeMap, "0.0");
                sb.AppendLine($"                float3 {v}_Image = float3({r},{g},{b});");
                varTypes[(node.name, "Image")] = "float3";
                break;
            }
            case "SEPRGB":
            case "SEPARATE_RGB":
            {
                string img = GetInputF3(node, "Image", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                sb.AppendLine($"                float3 {v}_tmp2 = {img};");
                sb.AppendLine($"                float {v}_R = {v}_tmp2.r;");
                sb.AppendLine($"                float {v}_G = {v}_tmp2.g;");
                sb.AppendLine($"                float {v}_B = {v}_tmp2.b;");
                varTypes[(node.name, "R")] = "float";
                varTypes[(node.name, "G")] = "float";
                varTypes[(node.name, "B")] = "float";
                break;
            }

            // ── Hue / Saturation / Value ───────────────────────────────────
            case "HUE_SAT":
            {
                string hue = GetInputF (node, "Hue",        linkMap, varTypes, nodeMap, "0.5");
                string sat = GetInputF (node, "Saturation", linkMap, varTypes, nodeMap, "1.0");
                string val = GetInputF (node, "Value",      linkMap, varTypes, nodeMap, "1.0");
                string fac = GetInputF (node, "Fac",        linkMap, varTypes, nodeMap, "1.0");
                string col = GetInputF3(node, "Color",      linkMap, varTypes, nodeMap, "float3(0.5,0.5,0.5)");
                sb.AppendLine($"                float3 {v}_hsv = mml_rgb2hsv({col});");
                sb.AppendLine($"                {v}_hsv.x = frac({v}_hsv.x + {hue} - 0.5);");
                sb.AppendLine($"                {v}_hsv.y = saturate({v}_hsv.y * {sat});");
                sb.AppendLine($"                {v}_hsv.z = {v}_hsv.z * {val};");
                sb.AppendLine($"                float3 {v}_Color = lerp({col}, mml_hsv2rgb({v}_hsv), {fac});");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Brightness / Contrast ──────────────────────────────────────
            case "BRIGHTCONTRAST":
            {
                string col    = GetInputF3(node, "Color",     linkMap, varTypes, nodeMap, "float3(0.5,0.5,0.5)");
                string bright = GetInputF (node, "Bright",    linkMap, varTypes, nodeMap, "0.0");
                string cont   = GetInputF (node, "Contrast",  linkMap, varTypes, nodeMap, "0.0");
                sb.AppendLine($"                float3 {v}_Color = ({col} - 0.5) * ({cont} + 1.0) + 0.5 + {bright};");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Gamma ──────────────────────────────────────────────────────
            case "GAMMA":
            {
                string col = GetInputF3(node, "Color", linkMap, varTypes, nodeMap, "float3(0.5,0.5,0.5)");
                string gam = GetInputF (node, "Gamma", linkMap, varTypes, nodeMap, "1.0");
                sb.AppendLine($"                float3 {v}_Color = pow(max(0,{col}), max(0.001, {gam}));");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── RGB Curves (simplified — identity) ────────────────────────
            case "CURVE_RGB":
            {
                string col = GetInputF3(node, "Color", linkMap, varTypes, nodeMap, "float3(0.5,0.5,0.5)");
                sb.AppendLine($"                float3 {v}_Color = {col}; // Curve approximated as pass-through");
                varTypes[(node.name, "Color")] = "float3";
                break;
            }

            // ── Float Curve (simplified) ───────────────────────────────────
            case "FLOAT_CURVE":
            {
                string val = GetInputF(node, "Value", linkMap, varTypes, nodeMap, "0.5");
                sb.AppendLine($"                float {v}_Value = {val}; // Curve approximated as pass-through");
                varTypes[(node.name, "Value")] = "float";
                break;
            }

            // ── Vector Math ────────────────────────────────────────────────
            case "VECT_MATH":
            {
                string a = GetInputF3(node, "Vector",   linkMap, varTypes, nodeMap, "float3(0,0,0)");
                string b = GetInputF3(node, "Vector_1", linkMap, varTypes, nodeMap, "float3(0,0,0)");
                b = GetInputF3ByIndex(node, 1, linkMap, varTypes, nodeMap, "float3(0,0,0)");
                string vexpr = (node.op ?? "ADD") switch
                {
                    "ADD"        => $"({a}+{b})",
                    "SUBTRACT"   => $"({a}-{b})",
                    "MULTIPLY"   => $"({a}*{b})",
                    "DIVIDE"     => $"({a}/({b}+1e-10))",
                    "CROSS_PRODUCT" => $"cross({a},{b})",
                    "DOT_PRODUCT"   => $"float3(dot({a},{b}),0,0)",
                    "NORMALIZE"  => $"normalize({a})",
                    "LENGTH"     => $"float3(length({a}),0,0)",
                    "SCALE"      => $"({a}*" + GetInputF(node,"Scale",linkMap,varTypes,nodeMap,"1.0") + ")",
                    _            => a,
                };
                sb.AppendLine($"                float3 {v}_Vector = {vexpr};");
                varTypes[(node.name, "Vector")] = "float3";
                break;
            }

            // ── RGB to BW ──────────────────────────────────────────────────
            case "RGBTOBW":
            {
                string col = GetInputF3(node, "Color", linkMap, varTypes, nodeMap, "float3(0.5,0.5,0.5)");
                sb.AppendLine($"                float {v}_Val = dot({col}, float3(0.2126,0.7152,0.0722));");
                varTypes[(node.name, "Val")] = "float";
                break;
            }

            // ── Normal Map ─────────────────────────────────────────────────
            case "NORMAL_MAP":
            {
                string col = GetInputF3(node, "Color", linkMap, varTypes, nodeMap, "float3(0.5,0.5,1.0)");
                // Unpack and flip Y for OpenGL→DirectX (Unity uses DX-style normal maps)
                sb.AppendLine($"                float3 {v}_unpacked = {col} * 2.0 - 1.0;");
                sb.AppendLine($"                {v}_unpacked.y = -{v}_unpacked.y; // NormalGL→DX");
                sb.AppendLine($"                float3 {v}_Normal = normalize({v}_unpacked);");
                varTypes[(node.name, "Normal")] = "float3";
                break;
            }

            // ── Reroute (pass-through) ─────────────────────────────────────
            case "REROUTE":
            {
                // Find upstream connection on first input socket
                string inSock = (node.inputs != null && node.inputs.Length > 0) ? node.inputs[0].name : "Input";
                if (linkMap.TryGetValue((node.name, inSock), out var rerouteSrc))
                {
                    string srcType = varTypes.GetValueOrDefault((rerouteSrc.Item1, rerouteSrc.Item2), "");
                    string srcVar  = GetOutputVar(rerouteSrc.Item1, rerouteSrc.Item2, varTypes);
                    if (srcVar != null)
                    {
                        if (srcType == "float3")
                        {
                            sb.AppendLine($"                float3 {v}_Output = {srcVar};");
                            varTypes[(node.name, "Output")] = "float3";
                        }
                        else
                        {
                            sb.AppendLine($"                float {v}_Output = {srcVar};");
                            varTypes[(node.name, "Output")] = "float";
                        }
                    }
                }
                break;
            }

            // ── Unknown / unsupported → emit comment ───────────────────────
            default:
                sb.AppendLine($"                // Unsupported node type: {node.type} ({node.name})");
                break;
        }
    }

    // ── Color Ramp Code Generation ──────────────────────────────────────────

    private static string ColorRampExpr(BubNodeData node, string facVar, out string alphaExpr)
    {
        if (node.cr_pos == null || node.cr_pos.Length < 2 || node.cr_col == null)
        {
            alphaExpr = "1.0";
            return "float3(0,0,0)";
        }
        int stopCount = node.cr_pos.Length;
        // Build piecewise linear HLSL inline
        var sb = new StringBuilder();
        sb.Append("(");
        string fv = facVar;
        // Default = first stop color
        float[] fc = StopColor(node.cr_col, 0);
        string cur = F3(fc);
        for (int i = 1; i < stopCount; i++)
        {
            float p0 = node.cr_pos[i - 1];
            float p1 = node.cr_pos[i];
            float[] c0 = StopColor(node.cr_col, i - 1);
            float[] c1 = StopColor(node.cr_col, i);
            float range = Math.Max(p1 - p0, 1e-6f);
            cur = $"lerp({F3(c0)},{F3(c1)},saturate(({fv}-{F(p0)})/{F(range)}))";
            if (i < stopCount - 1)
                cur = $"(({fv})<{F(p1)} ? {cur} : _CONT_)";
        }
        // Collapse placeholders
        string result = cur.Replace("_CONT_", F3(StopColor(node.cr_col, stopCount - 1)));
        // Alpha channel
        float[] fa0 = StopColor(node.cr_col, 0);
        float[] faN = StopColor(node.cr_col, stopCount - 1);
        alphaExpr = $"lerp({F(fa0.Length > 3 ? fa0[3] : 1f)},{F(faN.Length > 3 ? faN[3] : 1f)},saturate(({fv}-{F(node.cr_pos[0])})/{F(Math.Max(node.cr_pos[stopCount-1]-node.cr_pos[0],1e-6f))}))";
        return result;
    }

    private static float[] StopColor(float[] flat, int index)
    {
        int start = index * 4;
        if (start + 3 >= flat.Length) return new float[] { 1, 1, 1, 1 };
        return new float[] { flat[start], flat[start + 1], flat[start + 2], flat[start + 3] };
    }

    // ── Input Resolution ────────────────────────────────────────────────────

    /// Get float input (VALUE type) for a named socket.
    private static string GetInputF(BubNodeData node, string socketName,
        Dictionary<(string, string), (string, string)> linkMap,
        Dictionary<(string, string), string> varTypes,
        Dictionary<string, BubNodeData> nodeMap, string fallback)
    {
        if (linkMap.TryGetValue((node.name, socketName), out var src))
        {
            string outVar = GetOutputVar(src.Item1, src.Item2, varTypes);
            if (outVar != null)
            {
                string outType = varTypes.GetValueOrDefault((src.Item1, src.Item2), "float");
                if (outType == "float3") return $"dot({outVar},float3(0.2126,0.7152,0.0722))";
                if (outType == "float4") return $"{outVar}.r";
                return outVar;
            }
        }
        // Use default from node input definition
        if (node.inputs != null)
            foreach (var inp in node.inputs)
                if (inp.name == socketName && inp.type == "VALUE")
                    return F(inp.dv);
        return fallback;
    }

    /// Get float input by socket index (handles Blender's duplicate "Value" names).
    private static string GetInputFByIndex(BubNodeData node, int idx,
        Dictionary<(string, string), (string, string)> linkMap,
        Dictionary<(string, string), string> varTypes,
        Dictionary<string, BubNodeData> nodeMap, string fallback)
    {
        if (node.inputs == null || idx >= node.inputs.Length) return fallback;
        string socketName = node.inputs[idx].name;
        // Try with index suffix Blender uses: "Value", "Value", "Value" → use index
        // The linkMap uses socket name so just use the name directly for the first match
        // For subsequent duplicates Blender uses "Value" for idx 0 and same name for rest;
        // we differentiate using raw loop over links
        return GetInputF(node, socketName, linkMap, varTypes, nodeMap,
                         F(node.inputs[idx].type == "VALUE" ? node.inputs[idx].dv : 0f));
    }

    /// Get float3 input (RGBA/VECTOR type) for a named socket.
    private static string GetInputF3(BubNodeData node, string socketName,
        Dictionary<(string, string), (string, string)> linkMap,
        Dictionary<(string, string), string> varTypes,
        Dictionary<string, BubNodeData> nodeMap, string fallback)
    {
        if (linkMap.TryGetValue((node.name, socketName), out var src))
        {
            string outVar = GetOutputVar(src.Item1, src.Item2, varTypes);
            if (outVar != null)
            {
                string outType = varTypes.GetValueOrDefault((src.Item1, src.Item2), "float3");
                if (outType == "float")  return $"float3({outVar},{outVar},{outVar})";
                if (outType == "float4") return $"{outVar}.rgb";
                return outVar;
            }
        }
        if (node.inputs != null)
            foreach (var inp in node.inputs)
                if (inp.name == socketName && inp.dc != null && inp.dc.Length >= 3)
                    return F3(inp.dc);
        return fallback;
    }

    private static string GetInputF3ByIndex(BubNodeData node, int idx,
        Dictionary<(string, string), (string, string)> linkMap,
        Dictionary<(string, string), string> varTypes,
        Dictionary<string, BubNodeData> nodeMap, string fallback)
    {
        if (node.inputs == null || idx >= node.inputs.Length) return fallback;
        return GetInputF3(node, node.inputs[idx].name, linkMap, varTypes, nodeMap, fallback);
    }

    private static string GetOutputVar(string nodeName, string socketName,
        Dictionary<(string, string), string> varTypes)
    {
        if (varTypes.ContainsKey((nodeName, socketName)))
            return "v_" + SafeVar(nodeName) + "_" + SafeVar(socketName);
        return null;
    }

    // ── Math Expression Builder ──────────────────────────────────────────────

    private static string MathExpr(string op, string a, string b, string c) => op switch
    {
        "ADD"            => $"({a}+{b})",
        "SUBTRACT"       => $"({a}-{b})",
        "MULTIPLY"       => $"({a}*{b})",
        "DIVIDE"         => $"({a}/({b}+1e-10))",
        "POWER"          => $"pow(max(0,{a}),{b})",
        "LOGARITHM"      => $"log(max(1e-10,{a}))/log(max(1e-10,{b}))",
        "SQRT"           => $"sqrt(max(0,{a}))",
        "INVERSE_SQRT"   => $"(1.0/sqrt(max(1e-10,{a})))",
        "ABSOLUTE"       => $"abs({a})",
        "EXPONENT"       => $"exp({a})",
        "MINIMUM"        => $"min({a},{b})",
        "MAXIMUM"        => $"max({a},{b})",
        "LESS_THAN"      => $"(float)({a}<{b})",
        "GREATER_THAN"   => $"(float)({a}>{b})",
        "SIGN"           => $"sign({a})",
        "COMPARE"        => $"(float)(abs({a}-{b})<={c})",
        "SMOOTH_MIN"     => $"lerp({a},{b},0.5)",
        "SMOOTH_MAX"     => $"lerp({a},{b},0.5)",
        "ROUND"          => $"round({a})",
        "FLOOR"          => $"floor({a})",
        "CEIL"           => $"ceil({a})",
        "TRUNC"          => $"trunc({a})",
        "FRACT"          => $"frac({a})",
        "MODULO"         => $"fmod({a},max(1e-10,{b}))",
        "FLOORED_MODULO" => $"(fmod({a},max(1e-10,{b}))+max(1e-10,{b}))*fmod(1.0,1.0)",
        "WRAP"           => $"fmod({a},{b})",
        "SNAP"           => $"(floor({a}/{b}+1e-10)*{b})",
        "PINGPONG"       => $"abs(fmod({a},{b}*2.0)-{b})",
        "SINE"           => $"sin({a})",
        "COSINE"         => $"cos({a})",
        "TANGENT"        => $"tan({a})",
        "ARCSINE"        => $"asin(clamp({a},-1,1))",
        "ARCCOSINE"      => $"acos(clamp({a},-1,1))",
        "ARCTANGENT"     => $"atan({a})",
        "ARCTAN2"        => $"atan2({a},{b})",
        "SINH"           => $"sinh({a})",
        "COSH"           => $"cosh({a})",
        "TANH"           => $"tanh({a})",
        "RADIANS"        => $"radians({a})",
        "DEGREES"        => $"degrees({a})",
        "MULTIPLY_ADD"   => $"({a}*{b}+{c})",
        _                => a,
    };

    // ── Blend Mode Expression Builder ────────────────────────────────────────

    private static string BlendExpr(string blend, string fac, string c1, string c2) => blend switch
    {
        "MIX"          => $"lerp({c1},{c2},{fac})",
        "ADD"          => $"lerp({c1},{c1}+{c2},{fac})",
        "SUBTRACT"     => $"lerp({c1},{c1}-{c2},{fac})",
        "MULTIPLY"     => $"lerp({c1},{c1}*{c2},{fac})",
        "DIVIDE"       => $"lerp({c1},{c1}/({c2}+1e-6),{fac})",
        "DIFFERENCE"   => $"lerp({c1},abs({c1}-{c2}),{fac})",
        "DARKEN"       => $"lerp({c1},min({c1},{c2}),{fac})",
        "LIGHTEN"      => $"lerp({c1},max({c1},{c2}),{fac})",
        "SCREEN"       => $"lerp({c1},1-(1-{c1})*(1-{c2}),{fac})",
        "OVERLAY"      => $"lerp({c1},lerp(2*{c1}*{c2},1-2*(1-{c1})*(1-{c2}),step(0.5,{c1})),{fac})",
        "SOFT_LIGHT"   => $"lerp({c1},(1-2*{c2})*{c1}*{c1}+2*{c2}*{c1},{fac})",
        "EXCLUSION"    => $"lerp({c1},{c1}+{c2}-2*{c1}*{c2},{fac})",
        "COLOR_BURN"   => $"lerp({c1},1-min(1,(1-{c2})/({c1}+1e-6)),{fac})",
        "COLOR_DODGE"  => $"lerp({c1},min(1,{c2}/max(1e-6,1-{c1})),{fac})",
        "LINEAR_LIGHT" => $"lerp({c1},clamp({c1}+2*{c2}-1,0,1),{fac})",
        _              => $"lerp({c1},{c2},{fac})",
    };

    // ── Fallback shader ──────────────────────────────────────────────────────

    private static string FallbackShader(string shaderName) => $@"
Shader ""{shaderName}""
{{
    SubShader
    {{
        Tags {{ ""RenderPipeline""=""UniversalPipeline"" }}
        Pass
        {{
            Name ""ForwardLit""
            Tags {{ ""LightMode""=""UniversalForward"" }}
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            struct Attr {{ float4 pos:POSITION; }};
            struct Vary {{ float4 pos:SV_POSITION; }};
            Vary vert(Attr i){{ Vary o; o.pos=TransformObjectToHClip(i.pos.xyz); return o; }}
            half4 frag(Vary i):SV_Target {{ return half4(0.8,0.8,0.8,1); }}
            ENDHLSL
        }}
    }}
}}
";

    // ── Utilities ────────────────────────────────────────────────────────────

    private static string SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Unknown";
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static string SafeVar(string s) => SafeName(s);

    private static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    private static string F3(float[] c)
    {
        if (c == null || c.Length < 3) return "float3(0,0,0)";
        return $"float3({F(c[0])},{F(c[1])},{F(c[2])})";
    }

    private static float InpDV(BubNodeData node, string name)
    {
        if (node.inputs == null) return 0f;
        foreach (var i in node.inputs) if (i.name == name) return i.dv;
        return 0f;
    }

    private static float[] InpDC(BubNodeData node, string name)
    {
        if (node.inputs == null) return null;
        foreach (var i in node.inputs) if (i.name == name) return i.dc;
        return null;
    }

    private static void EnsureDir(string assetPath)
    {
        string full = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
        Directory.CreateDirectory(full);
    }
}
