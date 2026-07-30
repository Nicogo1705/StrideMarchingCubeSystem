# Stride Marching Cubes Voxel Terrain
[![Available on the Community Stride Asset Store](https://img.shields.io/badge/Community_Stride_Asset_Store-install-5b8def)](https://nicogo1705.github.io/AssetStore/a/com.nicogo.marching-cube-compute-shader)

Procedural voxel terrain for [Stride](https://www.stride3d.net/), meshed on the GPU
with a Marching Cubes compute shader. Fill chunks with one of three built-in noise
generators, mesh them with smooth gradient normals, and shade the surface with a
per-triangle **multi-material** colour blend — no textures required.

## What's in the box

| File | Role |
|------|------|
| `VoxelTerrain` | Drop-in `SyncScript`. Builds a finite chunked map, spread over a few frames. Pick a generator + size in Game Studio. |
| `MarchingCubesMesher` | The GPU mesher: uploads a `VoxelGrid`, dispatches the compute shader, returns a ready-to-render `MCVertex` buffer. |
| `VoxelGrid` | Packed chunk storage (density + material per voxel). Sampled from world coords so chunks are seamless. |
| `TerrainGenerators` | `ITerrainGenerator` + `Perlin`, `Ridged`, `Caves3D` — each parameterised (height, amplitude, frequency, octaves). |
| `Noise` | A small, dependency-free Perlin/FBM/ridged noise. |
| `TerrainPalette` | Per-material colours, uploaded as a tiny palette texture. Reskin the terrain by editing it. |
| `TerrainType` / `MCVertex` | The material enum and the 28-byte GPU vertex (Position, Normal, PackedMaterial). |
| `Effects/GenerateMarchingCube.sdsl` | The Marching Cubes compute shader (gradient normals, surface-material scan, reversed winding). |
| `Effects/TerrainDiffuse.sdsl` | Multi-material surface shader: blends the dominant + secondary palette colour per triangle. |

## Quick start (drop-in)

1. Reference `StrideMarchingCubeSystem` from your game project.
2. Add an empty entity, attach a **VoxelTerrain** component (category *Terrain*).
3. Choose a `Generator` (Perlin / Ridged / Caves3D), map size and noise settings.
4. Press play — the map builds itself.

## Use it directly (your own world)

```csharp
var gen = TerrainGeneratorBase.Create(NoiseKind.Ridged, seed: 1337,
    baseHeight: 24, amplitude: 40, frequency: 0.015f, octaves: 5);

using var mesher = new MarchingCubesMesher(Services, GraphicsDevice);
var grid = new VoxelGrid(32);
gen.Fill(grid, worldX0, worldY0, worldZ0);          // sample from absolute world coords

var mesh = mesher.GenerateMesh(grid, (Game)Game, isolevel: 0.5f);
if (mesh is var (vbo, count) && count > 0)
{
    // build a Mesh with MCVertex.Layout over `vbo` (DrawCount = count), assign your terrain material…
}
```

Write your own generator by implementing `ITerrainGenerator` (or subclassing
`TerrainGeneratorBase` and overriding `SurfaceHeight` / `Carve`), and set the per-voxel
`TerrainType` however you like — the shader resolves the dominant + secondary material
per surface triangle and blends their palette colours.

## How it works

- **Voxels → surface on the GPU.** `VoxelGrid` packs density (bits 0-7, `0` = solid,
  `255` = air) and material (bits 8-15) into one `ushort` per voxel — exactly the GPU
  layout. The compute shader marches each cube, interpolates edge vertices, derives a
  smooth normal from the density gradient, and appends triangles via an atomic counter.
- **Seamless chunks, no stitching.** Every sample (including the `Size+1` border row) is
  computed from absolute world coordinates, so neighbouring chunks share identical
  boundary values and line up exactly.
- **Multi-material.** For each surface triangle the shader scans its active edges at full
  voxel resolution to find the real surface material, picks a dominant + secondary
  material, and packs them (plus a blend weight) into the vertex. `TerrainDiffuse` blends
  the two palette colours — so slopes fade grass→rock, beaches fade sand→grass, etc.
- **Three generators.** *Perlin* (rolling hills), *Ridged* (mountains), *Caves3D* (hills
  carved by 3D noise into caves and overhangs). All share the height/depth material banding.

## Demo

Open `StrideMarchingCubeSystem.sln`, set **Demo.Windows** as startup and run. A 192×64×192
map builds over a few frames; fly around with WASD + right-mouse. Change the **VoxelTerrain**
component's `Generator`/`Seed`/`Amplitude` in Game Studio to reshape the world.

## License

MIT. See [LICENSE.md](LICENSE.md).
