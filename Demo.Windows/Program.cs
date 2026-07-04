using System;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
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
    }
}
