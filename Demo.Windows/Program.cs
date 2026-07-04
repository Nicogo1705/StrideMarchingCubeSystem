using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using StrideMarchingCubeSystem;

namespace Demo.Windows
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var game = new Game())
            {
                // Scripted showcase-video camera + gated generation (RECORD_VARIANT=1|2|3). No effect otherwise.
                if (int.TryParse(Environment.GetEnvironmentVariable("RECORD_VARIANT"), out var variant))
                {
                    game.WindowCreated += (_, _) => game.Window.Title = "StrideDemoRecord";
                    game.Script.AddTask(() => DriveAsync(game, variant));
                }

                game.Run();
            }
        }

        private static async Task DriveAsync(Game game, int variant)
        {
            Entity? camera = null;
            VoxelTerrain? terrain = null;
            while (camera is null || terrain is null)
            {
                await game.Script.NextFrame();
                var scene = game.SceneSystem.SceneInstance?.RootScene;
                if (scene is null)
                {
                    continue;
                }

                foreach (var entity in scene.Entities)
                {
                    if (entity.Get<CameraComponent>() is not null) camera = entity;
                    terrain ??= entity.Get<VoxelTerrain>();
                }
            }

            if (camera.Get<BasicCameraController>() is { } controller)
            {
                camera.Remove(controller);
            }

            game.Window.SetSize(new Int2(1920, 1080)); // capture the showcase in 1080p

            // Map: 10x3x10 chunks of 32 → 320x96x320 world units. The demo scene generates with
            // BaseHeight 44 / Amplitude 48, so the surface lives in ~[44..92].
            var center = new Vector3(160f, 55f, 160f);

            if (variant == 5)
            {
                await CaveDiveAsync(game, camera, terrain);
                return;
            }

            // Generation videos (variants 1-2): hold the build so it starts ON camera. A handful
            // of chunks are pre-built mid-warmup so the mesher's compute shaders compile off
            // camera, then the queue is released at ~24 chunks/s regardless of frame rate.
            const float warmupSeconds = 15f;
            var gatedBuild = variant is 1 or 2;
            var grantedFrames = 0;
            if (gatedBuild)
            {
                terrain.ChunksPerFrame = 1;
                terrain.Paused = true;
            }

            while (game.IsRunning)
            {
                var total = (float)game.UpdateTime.Total.TotalSeconds;
                var t = MathF.Max(0f, total - warmupSeconds);

                if (gatedBuild)
                {
                    var allowed = total < 4f ? 0f
                        : total < warmupSeconds ? 6f            // shader pre-warm
                        : 6f + t * 24f;                          // the show itself
                    var buildNow = grantedFrames < allowed;
                    terrain.Paused = !buildNow;
                    if (buildNow)
                    {
                        grantedFrames++;
                    }
                }

                Vector3 position, target;
                switch (variant)
                {
                    case 1: // aerial orbit while the map assembles
                        var angle = 1.6f + 0.09f * total;
                        position = center + new Vector3(240f * MathF.Cos(angle), 150f, 240f * MathF.Sin(angle));
                        target = center + new Vector3(0f, -5f, 0f);
                        break;
                    case 2: // spiral descent while chunks appear — never stops moving
                        var spiral = 2.6f + 0.11f * total;
                        var radius = MathF.Max(210f, 300f - 7f * t);
                        var height = MathF.Max(120f, 185f - 5f * t);
                        position = center + new Vector3(radius * MathF.Cos(spiral), height, radius * MathF.Sin(spiral));
                        target = center + new Vector3(0f, -12f, 0f); // frame mostly map, little sky
                        break;
                    case 4: // wide orbit of the finished map (built at full speed during warmup)
                        // Same geometry as the scene's default camera (the money shot): outside
                        // the massif, slightly above the peaks, horizon in frame.
                        var wide = 1.25f + 0.10f * total;
                        position = center + new Vector3(390f * MathF.Cos(wide), 135f, 390f * MathF.Sin(wide));
                        target = new Vector3(160f, 45f, 160f);
                        break;
                    default: // serpentine flyover of the finished terrain (built at full speed during warmup)
                        // RECORD_ALT overrides the altitude (generators peak at different heights).
                        if (!float.TryParse(Environment.GetEnvironmentVariable("RECORD_ALT"), out var altitude))
                        {
                            altitude = 70f;
                        }

                        position = new Vector3(
                            160f + 120f * MathF.Sin(0.11f * total),
                            altitude,
                            160f + 120f * MathF.Sin(0.22f * total));
                        // Look ahead along the motion, blended toward the map centre so the frame
                        // never fills with off-map void when the path skims an edge.
                        var vx = 120f * 0.11f * MathF.Cos(0.11f * total);
                        var vz = 120f * 0.22f * MathF.Cos(0.22f * total);
                        var speed = MathF.Sqrt(vx * vx + vz * vz) + 1e-4f;
                        var ahead = new Vector3(vx / speed, 0f, vz / speed);
                        var inward = Vector3.Normalize(new Vector3(center.X - position.X, 0f, center.Z - position.Z));
                        var look = Vector3.Normalize(ahead * 0.55f + inward * 0.45f);
                        target = position + new Vector3(look.X, -0.5f, look.Z);
                        break;
                }

                camera.Transform.Position = position;
                var dir = Vector3.Normalize(target - position);
                camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(
                    MathF.Atan2(-dir.X, -dir.Z), MathF.Asin(dir.Y), 0f);

                await game.Script.NextFrame();
            }
        }

        /// <summary>
        /// Cave fly-through (variant 5): rebuilds the scene's exact voxel field on the CPU,
        /// finds the longest connected air tunnel under the surface (BFS), and flies a torch-lit
        /// camera along the smoothed path — the only way to SHOW what Caves3D generates.
        /// </summary>
        private static async Task CaveDiveAsync(Game game, Entity camera, VoxelTerrain terrain)
        {
            // Rebuild the field with the SCENE's parameters, whatever they are.
            var generator = new Caves3DTerrainGenerator(terrain.Seed)
            {
                BaseHeight = terrain.BaseHeight,
                Amplitude = terrain.Amplitude,
                Frequency = terrain.Frequency,
                Octaves = terrain.Octaves,
                CaveFrequency = terrain.CaveFrequency,
                CaveThreshold = terrain.CaveThreshold,
            };

            // Sample a quadrant of the map (plenty to find a good tunnel, cheap on CPU).
            const int sx = 192, sy = 64, sz = 192;
            var air = new bool[sx, sy, sz];
            var chunk = new VoxelGrid(32);
            for (int cx = 0; cx < sx / 32; cx++)
            for (int cy = 0; cy < sy / 32; cy++)
            for (int cz = 0; cz < sz / 32; cz++)
            {
                generator.Fill(chunk, cx * 32, cy * 32, cz * 32);
                for (int x = 0; x < 32; x++)
                for (int y = 0; y < 32; y++)
                for (int z = 0; z < 32; z++)
                {
                    // Density convention: air amount × 255 → HIGH byte = air, LOW = solid.
                    air[cx * 32 + x, cy * 32 + y, cz * 32 + z] = chunk.GetDensity(x, y, z) >= 128;
                }
            }

            // Cave cells = air with clearance all around and solid somewhere above (a ceiling).
            bool Clear(int x, int y, int z) =>
                x >= 1 && y >= 1 && z >= 1 && x < sx - 1 && y < sy - 1 && z < sz - 1
                && air[x, y, z] && air[x - 1, y, z] && air[x + 1, y, z]
                && air[x, y - 1, z] && air[x, y + 1, z] && air[x, y, z - 1] && air[x, y, z + 1];

            // Enclosed = floor within 4 below AND ceiling within 30 above (open valleys have
            // sky all the way up — they must not count as "cave").
            bool Enclosed(int x, int y, int z)
            {
                bool floor = false, ceiling = false;
                for (int dy = 1; dy <= 4 && y - dy >= 0; dy++)
                {
                    if (!air[x, y - dy, z]) { floor = true; break; }
                }

                for (int dy = 2; dy <= 30 && y + dy < sy; dy++)
                {
                    if (!air[x, y + dy, z]) { ceiling = true; break; }
                }

                return floor && ceiling;
            }

            var inCave = new bool[sx, sy, sz];
            for (int x = 1; x < sx - 1; x++)
            for (int y = 6; y < 44; y++)
            for (int z = 1; z < sz - 1; z++)
            {
                inCave[x, y, z] = Clear(x, y, z) && Enclosed(x, y, z);
            }

            var path = FindLongestCavePath(inCave, sx, sy, sz);

            if (Environment.GetEnvironmentVariable("RECORD_DEBUG") is { Length: > 0 } debugFile)
            {
                int airCount = 0, caveCount = 0;
                for (int x = 0; x < sx; x++)
                for (int y = 6; y < 44; y++)
                for (int z = 0; z < sz; z++)
                {
                    if (air[x, y, z]) airCount++;
                    if (inCave[x, y, z]) caveCount++;
                }

                System.IO.File.WriteAllText(debugFile, $"air(6..44)={airCount} inCave={caveCount} path={path.Count}\n");
            }

            if (path.Count < 30)
            {
                return; // no usable tunnel — leave the default camera rather than fake it
            }

            // Torch on the camera — caves receive neither sun nor skybox light.
            var torch = new Entity("RecordTorch");
            torch.Add(new LightComponent
            {
                Type = new LightPoint { Radius = 28f, Color = new ColorRgbProvider(new Color3(1f, 0.82f, 0.55f)) },
                Intensity = 18f,
            });
            camera.Scene.Entities.Add(torch);

            const float warmupSeconds = 14f;
            const float speed = 5.5f; // metres per second along the tunnel
            while (game.IsRunning)
            {
                var total = (float)game.UpdateTime.Total.TotalSeconds;
                var t = MathF.Max(0f, total - warmupSeconds);

                var distance = t * speed;
                var position = SamplePath(path, distance);
                var lookAt = SamplePath(path, distance + 7f);

                camera.Transform.Position = position;
                torch.Transform.Position = position;
                var dir = lookAt - position;
                if (dir.LengthSquared() > 0.001f)
                {
                    dir = Vector3.Normalize(dir);
                    camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(
                        MathF.Atan2(-dir.X, -dir.Z), MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)), 0f);
                }

                await game.Script.NextFrame();
            }
        }

        private static List<Vector3> FindLongestCavePath(bool[,,] inCave, int sx, int sy, int sz)
        {
            (Int3 far, Dictionary<Int3, Int3> parents) Bfs(Int3 start)
            {
                var queue = new Queue<Int3>();
                var parents = new Dictionary<Int3, Int3> { [start] = start };
                queue.Enqueue(start);
                var last = start;
                Int3[] steps =
                [
                    new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
                ];
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    last = cell;
                    foreach (var step in steps)
                    {
                        var next = new Int3(cell.X + step.X, cell.Y + step.Y, cell.Z + step.Z);
                        if (next.X < 0 || next.Y < 0 || next.Z < 0 || next.X >= sx || next.Y >= sy || next.Z >= sz)
                            continue;
                        if (!inCave[next.X, next.Y, next.Z] || parents.ContainsKey(next))
                            continue;
                        parents[next] = cell;
                        queue.Enqueue(next);
                    }
                }

                return (last, parents);
            }

            // Seed from the LARGEST connected component — the first cave cell in scan order is
            // usually an isolated pocket.
            var visited = new HashSet<Int3>();
            Int3? seed = null;
            var bestSize = 0;
            for (int x = 0; x < sx; x++)
            for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++)
            {
                var cell = new Int3(x, y, z);
                if (!inCave[x, y, z] || visited.Contains(cell))
                {
                    continue;
                }

                var (_, component) = Bfs(cell);
                foreach (var visitedCell in component.Keys)
                {
                    visited.Add(visitedCell);
                }

                if (component.Count > bestSize)
                {
                    bestSize = component.Count;
                    seed = cell;
                }
            }

            if (seed is null)
            {
                return new List<Vector3>();
            }

            var (endA, _) = Bfs(seed.Value);
            var (endB, parents) = Bfs(endA);

            // Walk back from endB to endA, then smooth with a small moving average.
            var raw = new List<Vector3>();
            var current = endB;
            while (parents[current] != current)
            {
                raw.Add(new Vector3(current.X + 0.5f, current.Y + 0.5f, current.Z + 0.5f));
                current = parents[current];
            }

            raw.Reverse();
            var smooth = new List<Vector3>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var sum = Vector3.Zero;
                var n = 0;
                for (int j = Math.Max(0, i - 6); j <= Math.Min(raw.Count - 1, i + 6); j++)
                {
                    sum += raw[j];
                    n++;
                }

                smooth.Add(sum / n);
            }

            return smooth;
        }

        /// <summary>Point at <paramref name="distance"/> metres along the polyline (ping-pong at the ends).</summary>
        private static Vector3 SamplePath(List<Vector3> path, float distance)
        {
            var total = 0f;
            var lengths = new float[path.Count - 1];
            for (int i = 0; i < path.Count - 1; i++)
            {
                lengths[i] = (path[i + 1] - path[i]).Length();
                total += lengths[i];
            }

            var d = distance % (2f * total);
            if (d > total) d = 2f * total - d;

            for (int i = 0; i < lengths.Length; i++)
            {
                if (d <= lengths[i])
                {
                    return Vector3.Lerp(path[i], path[i + 1], lengths[i] < 1e-4f ? 0f : d / lengths[i]);
                }

                d -= lengths[i];
            }

            return path[path.Count - 1];
        }
    }
}
