using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using System.Collections.Generic;
using GpuBuffer = Stride.Graphics.Buffer;

namespace StrideMarchingCubeSystem;

/// <summary>
/// Drop-in voxel terrain: attach this to an entity, pick a generator + size, press play,
/// and a finite marching-cubes map is built chunk-by-chunk (spread over a few frames so
/// there's no long hitch). Each chunk becomes a child entity with a GPU-generated mesh
/// shaded by the multi-material palette. Density is sampled from absolute world
/// coordinates, so chunks are seamless with no stitching pass.
///
/// For an infinite/streamed world, ignore this component and drive
/// <see cref="MarchingCubesMesher"/> + an <see cref="ITerrainGenerator"/> yourself.
/// </summary>
[ComponentCategory("Terrain")]
public sealed class VoxelTerrain : SyncScript
{
    /// <summary>Voxels per chunk edge. Must be a positive multiple that suits your map.</summary>
    public int ChunkSize { get; set; } = 32;

    /// <summary>Map size in chunks along X.</summary>
    public int MapChunksX { get; set; } = 6;
    /// <summary>Map size in chunks along Y (height).</summary>
    public int MapChunksY { get; set; } = 2;
    /// <summary>Map size in chunks along Z.</summary>
    public int MapChunksZ { get; set; } = 6;

    /// <summary>Which built-in noise generator to use.</summary>
    public NoiseKind Generator { get; set; } = NoiseKind.Perlin;

    /// <summary>Random seed.</summary>
    public int Seed { get; set; } = 1337;

    /// <summary>Average terrain height (world units).</summary>
    public float BaseHeight { get; set; } = 20f;
    /// <summary>Vertical scale of the terrain.</summary>
    public float Amplitude { get; set; } = 20f;
    /// <summary>Horizontal noise frequency — smaller = broader features.</summary>
    public float Frequency { get; set; } = 0.02f;
    /// <summary>Fractal octaves.</summary>
    public int Octaves { get; set; } = 4;

    /// <summary>How many chunks to build per frame (spreads the initial cost).</summary>
    public int ChunksPerFrame { get; set; } = 4;

    /// <summary>Suspends chunk building while true (streaming gates, cutscenes, benchmarks…).
    /// Already-built chunks stay visible; the pending queue resumes where it left off.</summary>
    public bool Paused { get; set; }

    private const float Isolevel = 0.5f;
    private const byte IsolevelByte = 128;

    private bool _initialized;
    private Material? _material;
    private Texture? _terrainTexture;
    private MarchingCubesMesher? _mesher;
    private ITerrainGenerator? _generator;
    private Queue<Int3>? _pending;

    private static readonly ObjectParameterKey<Texture?> KeyTerrainTextures =
        ParameterKeys.NewObject<Texture?>(null, "TerrainDiffuse.TerrainTextures");

    public override void Update()
    {
        if (Paused)
            return;

        if (!_initialized)
            Initialize();

        int budget = System.Math.Max(1, ChunksPerFrame);
        while (budget-- > 0 && _pending!.Count > 0)
            BuildChunk(_pending.Dequeue());

        if (_pending!.Count == 0 && _mesher != null)
        {
            // Map fully built — free the mesher's GPU scratch buffers.
            _mesher.Dispose();
            _mesher = null;
        }
    }

    private void Initialize()
    {
        _terrainTexture = TerrainTextures.BuildArray(GraphicsDevice, Seed);

        var diffuse = new ComputeShaderClassColor { MixinReference = "TerrainDiffuse" };
        _material = Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(diffuse),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            }
        });
        _material.Passes[0].Parameters.Set(KeyTerrainTextures, _terrainTexture);

        _generator = TerrainGeneratorBase.Create(Generator, Seed, BaseHeight, Amplitude, Frequency, Octaves);

        // Size the mesher for the worst-case single-chunk surface.
        int maxVerts = System.Math.Max(60_000, ChunkSize * ChunkSize * ChunkSize / 4);
        _mesher = new MarchingCubesMesher(Services, GraphicsDevice, maxVerts);

        _pending = new Queue<Int3>();
        for (int cx = 0; cx < MapChunksX; cx++)
            for (int cy = 0; cy < MapChunksY; cy++)
                for (int cz = 0; cz < MapChunksZ; cz++)
                    _pending.Enqueue(new Int3(cx, cy, cz));

        _initialized = true;
    }

    private void BuildChunk(Int3 chunk)
    {
        int wx = chunk.X * ChunkSize;
        int wy = chunk.Y * ChunkSize;
        int wz = chunk.Z * ChunkSize;

        var grid = new VoxelGrid(ChunkSize);
        _generator!.Fill(grid, wx, wy, wz);

        // Optional build diagnostics (set VOXEL_DEBUG_LOG to a file path).
        var debugLog = System.Environment.GetEnvironmentVariable("VOXEL_DEBUG_LOG");

        // Uniform chunks (all solid or all air) have no surface — skip.
        if (grid.IsUniform(IsolevelByte))
        {
            if (debugLog is not null)
                System.IO.File.AppendAllText(debugLog, $"{chunk.X},{chunk.Y},{chunk.Z} uniform\n");
            return;
        }

        var result = _mesher!.GenerateMesh(grid, (Game)Game, Isolevel);
        if (debugLog is not null)
            System.IO.File.AppendAllText(debugLog, $"{chunk.X},{chunk.Y},{chunk.Z} verts={result?.vertexCount ?? 0}\n");
        if (result == null) return;

        var (vbo, count) = result.Value;

        var meshDraw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            VertexBuffers = new[] { new VertexBufferBinding(vbo, MCVertex.Layout, count) },
            DrawCount = count,
        };

        var model = new Model();
        model.Add(new MaterialInstance(_material));
        model.Add(new Mesh
        {
            Draw = meshDraw,
            MaterialIndex = 0,
            BoundingBox = new BoundingBox(Vector3.Zero, new Vector3(ChunkSize)),
        });

        var chunkEntity = new Entity($"Chunk_{chunk.X}_{chunk.Y}_{chunk.Z}")
        {
            Transform = { Position = new Vector3(wx, wy, wz) },
        };
        chunkEntity.Add(new ModelComponent { Model = model, IsShadowCaster = true });
        Entity.AddChild(chunkEntity);
    }

    public override void Cancel()
    {
        _mesher?.Dispose();
        _mesher = null;
    }
}
