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

            // Map: 10x3x10 chunks of 32 → 320x96x320 world units, surface ≤ ~48.
            var center = new Vector3(160f, 25f, 160f);

            if (variant is 1 or 2)
            {
                // Stretch the build over ~8-10 s so the chunk sweep reads on video (the capture
                // starts early and gets trimmed afterwards).
                terrain.ChunksPerFrame = 1;
            }

            while (game.IsRunning)
            {
                var t = (float)game.UpdateTime.Total.TotalSeconds;

                Vector3 position, target;
                switch (variant)
                {
                    case 1: // aerial orbit while the map assembles
                        var angle = 1.8f + 0.07f * t;
                        position = center + new Vector3(280f * MathF.Cos(angle), 190f, 280f * MathF.Sin(angle));
                        target = center;
                        break;
                    case 2: // closer push-in while chunks appear
                        var k = Math.Clamp((t - 5f) / 14f, 0f, 1f);
                        position = Vector3.Lerp(new Vector3(-60f, 150f, 430f), new Vector3(80f, 90f, 300f), k);
                        target = center;
                        break;
                    default: // low orbit around the finished terrain (generated at full speed during warmup)
                        var low = 0.09f * t;
                        position = center + new Vector3(210f * MathF.Cos(low), 55f, 210f * MathF.Sin(low));
                        target = new Vector3(160f, 30f, 160f);
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
