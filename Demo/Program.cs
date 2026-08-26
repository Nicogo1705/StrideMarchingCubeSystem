using Stride.Engine;

// The whole demo: open the game, which loads the scene named in GameSettings. Anything worth
// showing belongs in that scene or in a script — this file exists so `dotnet run` has an entry
// point, and so the store can launch the demo the same way on every operating system.
using var game = new Game();
game.Run();
