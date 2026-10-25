using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// GPU compute shader implementation of L1 SH radiance hints baking using Godot's <see cref="RenderingDevice"/>.
/// Dispatches Pass 1 (Direct Sun + Sky + Entity Lights) and Pass 2 (Single-Bounce Indirect GI)
/// directly in VRAM and exposes output to materials via <see cref="Texture3DRD"/> (or falls back to CPU baking).
/// </summary>
public sealed class GpuRadianceBaker : IDisposable
{
    public const string DirectShaderPath = "res://World/Shaders/radiance_direct.glsl";
    public const string BounceShaderPath = "res://World/Shaders/radiance_bounce.glsl";

    private readonly RenderingDevice? _rd;
    private Rid _directPipeline;
    private Rid _bouncePipeline;
    private Rid _directShader;
    private Rid _bounceShader;

    private Rid _voxelGridTex;
    private Rid _probeDirectTex;
    private Rid _probeFinalTex;
    private Rid _probeDirectSampler;
    private Rid _voxelSampler;
    private Rid _lightBuffer;

    private Rid _pass1UniformSet;
    private Rid _pass2UniformSet;

    private int _allocatedProbeRes;
    private int _allocatedVoxelRes;
    private int _allocatedLightCapacity;

    public Texture3Drd? OutputTextureRd { get; private set; }
    public Rid ProbeFinalRid => _probeFinalTex;
    public bool IsGpuAvailable => _rd != null && _directPipeline.IsValid && _bouncePipeline.IsValid;

    public GpuRadianceBaker()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd != null)
        {
            try
            {
                InitShaders();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GpuRadianceBaker] Failed to initialize GPU compute pipelines: {ex.Message}. Falling back to CPU.");
                Cleanup();
            }
        }
    }

    private void InitShaders()
    {
        if (_rd == null) return;

        _directShader = CompileShader(DirectShaderPath);
        _bounceShader = CompileShader(BounceShaderPath);

        _directPipeline = _rd.ComputePipelineCreate(_directShader);
        _bouncePipeline = _rd.ComputePipelineCreate(_bounceShader);

        // Linear clamp samplers for 3D textures
        var samplerState = new RDSamplerState
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MipFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        };
        _voxelSampler = _rd.SamplerCreate(samplerState);
        _probeDirectSampler = _rd.SamplerCreate(samplerState);
    }

    private Rid CompileShader(string path)
    {
        var shaderFile = ResourceLoader.Load<RDShaderFile>(path);
        if (shaderFile != null)
        {
            var spirv = shaderFile.GetSpirV();
            if (spirv != null)
            {
                if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
                    throw new InvalidOperationException($"Shader compilation error in {path}: {spirv.CompileErrorCompute}");
                return _rd!.ShaderCreateFromSpirV(spirv);
            }
        }

        var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            throw new InvalidOperationException($"Could not open shader file: {path}");

        string glsl = file.GetAsText();
        var shaderSource = new RDShaderSource
        {
            SourceCompute = glsl,
            Language = RenderingDevice.ShaderLanguage.Glsl,
        };

        var rawSpirv = _rd!.ShaderCompileSpirVFromSource(shaderSource);
        if (!string.IsNullOrEmpty(rawSpirv.CompileErrorCompute))
            throw new InvalidOperationException($"Shader compilation error in {path}: {rawSpirv.CompileErrorCompute}");

        return _rd.ShaderCreateFromSpirV(rawSpirv);
    }

    private void EnsureResources(int probeRes, int voxelRes, int lightCount)
    {
        if (_rd == null) return;

        bool probeChanged = _probeFinalTex.Id == 0 || _allocatedProbeRes != probeRes;
        bool voxelChanged = _voxelGridTex.Id == 0 || _allocatedVoxelRes != voxelRes;
        bool lightChanged = _lightBuffer.Id == 0 || _allocatedLightCapacity < Math.Max(1, lightCount);

        if (voxelChanged)
        {
            if (_voxelGridTex.IsValid) _rd.FreeRid(_voxelGridTex);

            var voxelFormat = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = (uint)voxelRes,
                Height = (uint)voxelRes,
                Depth = (uint)voxelRes,
                TextureType = RenderingDevice.TextureType.Type3D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit,
            };
            _voxelGridTex = _rd.TextureCreate(voxelFormat, new RDTextureView());
            _allocatedVoxelRes = voxelRes;
        }

        if (probeChanged)
        {
            if (_probeDirectTex.IsValid) _rd.FreeRid(_probeDirectTex);
            if (_probeFinalTex.IsValid) _rd.FreeRid(_probeFinalTex);

            var probeFormat = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                Width = (uint)probeRes,
                Height = (uint)probeRes,
                Depth = (uint)probeRes,
                TextureType = RenderingDevice.TextureType.Type3D,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };

            _probeDirectTex = _rd.TextureCreate(probeFormat, new RDTextureView());
            _probeFinalTex = _rd.TextureCreate(probeFormat, new RDTextureView());
            _allocatedProbeRes = probeRes;

            OutputTextureRd = new Texture3Drd { TextureRdRid = _probeFinalTex };
        }

        if (lightChanged)
        {
            if (_lightBuffer.IsValid) _rd.FreeRid(_lightBuffer);

            _allocatedLightCapacity = Math.Max(16, lightCount);
            uint bufferBytes = (uint)(16 + _allocatedLightCapacity * 48); // 16 bytes header + 48 bytes per GpuLight
            _lightBuffer = _rd.StorageBufferCreate(bufferBytes);
        }

        if (probeChanged || voxelChanged || lightChanged)
        {
            RebuildUniformSets();
        }
    }

    private void RebuildUniformSets()
    {
        if (_rd == null) return;

        // Pass 1 Uniform Set:
        // Binding 0: sampler3D voxel_grid
        // Binding 1: LightBuffer SSBO
        // Binding 2: image3D probe_direct_tex
        var uVoxel = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = 0,
        };
        uVoxel.AddId(_voxelSampler);
        uVoxel.AddId(_voxelGridTex);

        var uLights = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1,
        };
        uLights.AddId(_lightBuffer);

        var uDirectImg = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 2,
        };
        uDirectImg.AddId(_probeDirectTex);

        _pass1UniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uVoxel, uLights, uDirectImg }, _directShader, 0);

        // Pass 2 Uniform Set:
        // Binding 0: sampler3D voxel_grid
        // Binding 1: sampler3D probe_direct_sampler
        // Binding 2: image3D probe_final_tex
        var uDirectSamp = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = 1,
        };
        uDirectSamp.AddId(_probeDirectSampler);
        uDirectSamp.AddId(_probeDirectTex);

        var uFinalImg = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 2,
        };
        uFinalImg.AddId(_probeFinalTex);

        _pass2UniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uVoxel, uDirectSamp, uFinalImg }, _bounceShader, 0);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct GpuLight
    {
        public Vector4 PositionRange; // xyz = world pos, w = range
        public Vector4 ColorSpotCos;  // rgb = linear color, w = cos(spot_angle) (-1.0 for omni)
        public Vector4 SpotDir;       // xyz = spot dir, w = 1.0
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct Pass1PushConstants
    {
        public Vector3 GridOrigin;
        public float GridExtent;
        public Vector3 SunDir;
        public float SunScale;
        public Vector3 SunColor;
        public uint ProbeRes;
        public Vector3 SkyColor;
        public uint SkyRays;
        public Vector3I DirtyMin;
        public float MaxRayDist;
        public Vector3I DirtyMax;
        public uint VoxelRes;
        public Vector3 AmbientColor;
        public uint PadEnd;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct Pass2PushConstants
    {
        public Vector3 GridOrigin;
        public float GridExtent;
        public Vector3 SkyTint;
        public uint ProbeRes;
        public uint SkyRays;
        public float MaxRayDist;
        public uint VoxelRes;
        public uint Pad0;
        public Vector3I DirtyMin;
        public int Pad1;
        public Vector3I DirtyMax;
        public int Pad2;
    }

    /// <summary>
    /// Execute GPU compute baking passes for Pass 1 (Direct) and Pass 2 (Bounce).
    /// </summary>
    public bool Bake(
        VoxelGrid voxelGrid,
        RadianceGrid radianceGrid,
        MapConfig config,
        IReadOnlyList<DirectLightSource>? directLights = null,
        Vector3I? dirtyMin = null,
        Vector3I? dirtyMax = null,
        int skyRays = 16,
        float? maxRayDistance = null)
    {
        if (!IsGpuAvailable || _rd == null)
            return false;

        int lightCount = directLights?.Count ?? 0;
        EnsureResources(radianceGrid.Resolution, voxelGrid.Resolution, lightCount);

        // Upload voxel grid buffer to 3D texture
        _rd.TextureUpdate(_voxelGridTex, 0, voxelGrid.Data);

        // Upload entity lights to SSBO
        UploadLights(directLights);

        int res = radianceGrid.Resolution;
        Vector3I minCell = dirtyMin ?? Vector3I.Zero;
        Vector3I maxCell = dirtyMax ?? new Vector3I(res - 1, res - 1, res - 1);

        Color ambColor = RadianceVolume.DecodeColor(config.Ambient);
        Color skyColor = RadianceVolume.DecodeColor(config.Skylight);
        Color sunColor = RadianceVolume.DecodeColor(config.Sunlight);
        Vector3 sunDirSauer = RadianceVolume.CalculateSunlightDirSauer(config.SunlightYaw, config.SunlightPitch);
        Vector3 sunDirWorld = RadianceGrid.SauerToWorld(sunDirSauer);
        float maxRay = maxRayDistance is > 0f ? maxRayDistance.Value : radianceGrid.Extent * 1.5f;

        // ---- DISPATCH PASS 1 ----
        var p1 = new Pass1PushConstants
        {
            GridOrigin = radianceGrid.Origin,
            GridExtent = radianceGrid.Extent,
            SunDir = sunDirWorld,
            SunScale = config.SunlightScale,
            SunColor = new Vector3(sunColor.R, sunColor.G, sunColor.B),
            ProbeRes = (uint)res,
            SkyColor = new Vector3(skyColor.R, skyColor.G, skyColor.B),
            SkyRays = (uint)Math.Max(1, skyRays),
            DirtyMin = minCell,
            MaxRayDist = maxRay,
            DirtyMax = maxCell,
            VoxelRes = (uint)voxelGrid.Resolution,
            AmbientColor = new Vector3(ambColor.R, ambColor.G, ambColor.B),
        };

        byte[] p1Bytes = StructureToByteArray(p1);
        int groupsX = (maxCell.X - minCell.X + 4) / 4;
        int groupsY = (maxCell.Y - minCell.Y + 4) / 4;
        int groupsZ = (maxCell.Z - minCell.Z + 4) / 4;

        long computeList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(computeList, _directPipeline);
        _rd.ComputeListBindUniformSet(computeList, _pass1UniformSet, 0);
        _rd.ComputeListSetPushConstant(computeList, p1Bytes, (uint)p1Bytes.Length);
        _rd.ComputeListDispatch(computeList, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
        _rd.ComputeListEnd();

        // Dispatched compute passes automatically order through buffer/image barriers in RenderingDevice
        // ---- DISPATCH PASS 2 ----
        var p2 = new Pass2PushConstants
        {
            GridOrigin = radianceGrid.Origin,
            GridExtent = radianceGrid.Extent,
            SkyTint = new Vector3(skyColor.R, skyColor.G, skyColor.B),
            ProbeRes = (uint)res,
            SkyRays = (uint)Math.Max(1, skyRays),
            MaxRayDist = maxRay,
            VoxelRes = (uint)voxelGrid.Resolution,
            DirtyMin = minCell,
            DirtyMax = maxCell,
        };

        byte[] p2Bytes = StructureToByteArray(p2);
        computeList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(computeList, _bouncePipeline);
        _rd.ComputeListBindUniformSet(computeList, _pass2UniformSet, 0);
        _rd.ComputeListSetPushConstant(computeList, p2Bytes, (uint)p2Bytes.Length);
        _rd.ComputeListDispatch(computeList, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
        _rd.ComputeListEnd();

        return true;
    }

    private void UploadLights(IReadOnlyList<DirectLightSource>? lights)
    {
        if (_rd == null || _lightBuffer.Id == 0) return;

        int count = lights?.Count ?? 0;
        int bufferBytes = 16 + count * 48;
        byte[] bytes = new byte[Math.Max(64, bufferBytes)];

        // Header: uint light_count, 3 uint pad
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), count);

        if (lights != null)
        {
            for (int i = 0; i < count; i++)
            {
                var light = lights[i];
                int offset = 16 + i * 48;

                // Position in Godot world space
                Vector3 worldPos = RadianceGrid.SauerToWorld(light.Position);
                Span<byte> slice = bytes.AsSpan(offset, 48);

                // GpuLight:
                // position_range (vec4): xyz = pos, w = radius
                MemoryMarshal.Write(slice.Slice(0, 16), in worldPos);
                BitConverter.TryWriteBytes(slice.Slice(12, 4), light.Radius);

                // color_spotcos (vec4): rgb = color, w = spot_cos (-1.0 if omni)
                var col = new Vector3(light.Color.R, light.Color.G, light.Color.B);
                MemoryMarshal.Write(slice.Slice(16, 16), in col);
                float spotCos = light.IsSpotlight ? light.SpotCosAngle : -1.0f;
                BitConverter.TryWriteBytes(slice.Slice(28, 4), spotCos);

                // spot_dir (vec4): xyz = spot dir world, w = 1.0
                Vector3 spotDirWorld = light.IsSpotlight ? RadianceGrid.SauerToWorld(light.SpotDirection) : Vector3.Zero;
                MemoryMarshal.Write(slice.Slice(32, 16), in spotDirWorld);
                float spotAtten = 1.0f;
                BitConverter.TryWriteBytes(slice.Slice(44, 4), spotAtten);
            }
        }

        _rd.BufferUpdate(_lightBuffer, 0, (uint)bytes.Length, bytes);
    }

    /// <summary>
    /// Reads back probe values from VRAM to CPU <see cref="RadianceGrid"/> (for tests and verification).
    /// </summary>
    public void ReadbackToGrid(RadianceGrid targetGrid)
    {
        if (_rd == null || !_probeFinalTex.IsValid) return;

        byte[] raw = _rd.TextureGetData(_probeFinalTex, 0);
        Span<Half> halves = MemoryMarshal.Cast<byte, Half>(raw.AsSpan());

        int res = targetGrid.Resolution;
        int halfIdx = 0;

        for (int z = 0; z < res; z++)
        {
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float r = (float)halves[halfIdx++];
                    float g = (float)halves[halfIdx++];
                    float b = (float)halves[halfIdx++];
                    float a = (float)halves[halfIdx++];

                    targetGrid[x, y, z] = new RadianceProbe(new Vector3(r, g, b), a);
                }
            }
        }
    }

    private static byte[] StructureToByteArray<T>(T str) where T : struct
    {
        int size = Marshal.SizeOf(str);
        byte[] arr = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return arr;
    }

    private void Cleanup()
    {
        if (_rd == null) return;

        if (_voxelGridTex.IsValid) _rd.FreeRid(_voxelGridTex);
        if (_probeDirectTex.IsValid) _rd.FreeRid(_probeDirectTex);
        if (_probeFinalTex.IsValid) _rd.FreeRid(_probeFinalTex);
        if (_voxelSampler.IsValid) _rd.FreeRid(_voxelSampler);
        if (_probeDirectSampler.IsValid) _rd.FreeRid(_probeDirectSampler);
        if (_lightBuffer.IsValid) _rd.FreeRid(_lightBuffer);

        if (_directPipeline.IsValid) _rd.FreeRid(_directPipeline);
        if (_bouncePipeline.IsValid) _rd.FreeRid(_bouncePipeline);
        if (_directShader.IsValid) _rd.FreeRid(_directShader);
        if (_bounceShader.IsValid) _rd.FreeRid(_bounceShader);
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
