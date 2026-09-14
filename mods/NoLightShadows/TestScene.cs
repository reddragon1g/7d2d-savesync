using System.Collections.Generic;
using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// Builds a room with a light in it, so the mask can be looked at rather than reasoned about.
///
/// The mask's geometry is checked by SelfTest against the ground and the sky, which needs nothing
/// built. But that only proves the rays are cast in the right directions - it says nothing about
/// whether the cubemap those rays produce is actually painted onto the light by the renderer. A
/// wrong face mapping, a cookie the engine silently ignores, a texture format it will not sample:
/// all of those pass the geometry test and still produce a light that shines through walls.
///
/// So this builds something unambiguous to photograph. A sealed stone room with one doorway facing
/// the player, and a candle inside:
///
///   - light pouring out of the doorway says the light is on and the test means something
///   - dark walls say the mask is working
///   - a glowing room, lit through its own walls, says it is not
///
/// The doorway matters as much as the walls. Without it a dark room proves nothing, because a
/// light that failed to turn on looks exactly the same.
///
/// Only ever runs when asked for on the command line, and only in a throwaway world - it writes
/// blocks into whatever save is loaded.
/// </summary>
public static class TestScene
{
    private const string Flag = "-NoLightShadowsBuildTest";

    /// <summary>
    /// Candidates for the walls, tried in order until one resolves to a real block.
    ///
    /// The first attempt used terrStone and produced a room that existed in the block data and
    /// rendered as nothing at all - terrain blocks are rebuilt through the terrain mesh rather
    /// than the block mesh, so placing one in mid air leaves it invisible. The candle placed at
    /// the same moment rendered perfectly, which is how the difference showed up.
    ///
    /// Rather than guess again, the list is tried and the winner logged. Block names in this game
    /// change between versions and "shapes" blocks need a variant suffix whose spelling is not
    /// obvious from the data files.
    /// </summary>
    private static readonly string[] WallCandidates =
    {
        "concreteShapes:Cube", "cobblestoneShapes:Cube", "steelShapes:Cube", "brickShapes:Cube",
        "concreteBlock", "cobblestoneBlock", "terrStone",
    };

    private const string LightBlock = "candleTableLightPlayer";

    private static bool _built;

    public static bool Requested
    {
        get
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.StartsWith(Flag, System.StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }

    /// <summary>
    /// Puts the room a few paces in front of wherever the player is looking.
    ///
    /// In front rather than around them, because a room built on top of somebody buries them in
    /// stone - and a screenshot taken from inside a solid block is not a useful photograph.
    /// </summary>
    public static void Build(World world, EntityPlayerLocal player)
    {
        if (_built) return;
        _built = true;

        // Midnight, because in daylight this test means nothing: the sun lights the outside of the
        // room far more brightly than any candle could, and "is that wall lit" stops having an
        // answer. The first attempt at this was photographed at midday and showed nothing at all.
        world.SetTimeJump(GameUtils.DayTimeToWorldTime(2, 0, 0));

        // Where the room went last time, if it has been built before.
        //
        // Comparing the mask on against the mask off only means something if both photographs are
        // of the same room from the same spot. Rebuilding relative to the player drifts nine
        // blocks further every run, because the player is left standing where the camera was put.
        int cx, cy, cz;

        if (!ReadRemembered(out cx, out cy, out cz))
        {
            var playerPos = player.position;

            // Straight out along +Z rather than wherever the player happens to face, so the
            // room's position is known and the camera can be pointed at it.
            var centre = playerPos + Vector3.forward * 9f;

            cx = Mathf.FloorToInt(centre.x);
            cy = Mathf.FloorToInt(playerPos.y);
            cz = Mathf.FloorToInt(centre.z);

            Remember(cx, cy, cz);
        }

        var light = Block.GetBlockValue(LightBlock, true);

        var wall = BlockValue.Air;
        string wallName = null;

        foreach (var candidate in WallCandidates)
        {
            var tried = Block.GetBlockValue(candidate, true);
            if (tried.isair) continue;

            wall = tried;
            wallName = candidate;
            break;
        }

        if (wallName == null || light.isair)
        {
            Log.Error($"[NoLightShadows] test scene: no wall block resolved from {WallCandidates.Length} "
                      + $"candidates, or '{LightBlock}' is missing");
            return;
        }

        Log.Out($"[NoLightShadows] using '{wallName}' for the walls");

        Log.Out($"[NoLightShadows] building a test room at {cx}, {cy}, {cz}");

        // A hollow box, walls one block thick, tall enough to stand in.
        const int half = 3;
        const int height = 4;

        // The doorway, punched through the wall nearest the camera so the inside is visible.
        int doorX = cx;
        int doorZ = cz - half;

        // Gathered and submitted together through the game's own block-change path. World.SetBlock
        // writes the data and leaves the chunk mesh alone, which is why the first version of this
        // built a room nobody could see; SetBlocksRPC is what the game itself calls, and it
        // rebuilds the mesh as a matter of course.
        var changes = new List<BlockChangeInfo>();

        for (int x = cx - half; x <= cx + half; x++)
        for (int z = cz - half; z <= cz + half; z++)
        for (int y = cy; y <= cy + height; y++)
        {
            bool onShell = x == cx - half || x == cx + half
                           || z == cz - half || z == cz + half
                           || y == cy || y == cy + height;

            bool isDoorway = x == doorX && z == doorZ && (y == cy + 1 || y == cy + 2);
            bool isCandle = x == cx && z == cz && y == cy + 1;

            var what = isCandle ? light
                     : isDoorway ? BlockValue.Air
                     : onShell ? wall
                     : BlockValue.Air;

            changes.Add(new BlockChangeInfo(new BlockValueRef(new Vector3i(x, y, z)), what, true));
        }

        world.SetBlocksRPC(changes);

        // Face the room, and stand far enough back to see all of it. Without this the camera
        // points wherever the player happened to spawn looking, which the first attempt proved
        // is usually at a building somewhere else entirely.
        player.SetPosition(new Vector3(cx + 0.5f, cy + 1.5f, cz - 9f));
        player.SetRotationAndStopTurning(new Vector3(0f, 0f, 0f));

        // Read back what was written. "SetBlock returned without throwing" is not the same as
        // "there is a wall there", and the difference between those two is the difference between
        // debugging a renderer and debugging a block placement - which are not the same evening.
        // Read back a spot that is definitely wall and definitely NOT the doorway. The first
        // version of this check sampled the doorway itself and reported "air", which looked
        // exactly like the walls having failed to place.
        var wallCheck = world.GetBlock(new Vector3i(cx + 1, cy + 2, cz - half));
        var lightCheck = world.GetBlock(new Vector3i(cx, cy + 1, cz));

        Log.Out($"[NoLightShadows] readback: wall block at the near face is "
                + $"'{wallCheck.Block?.GetBlockName() ?? "null"}' (air={wallCheck.isair}), "
                + $"light is '{lightCheck.Block?.GetBlockName() ?? "null"}' (air={lightCheck.isair})");

        Log.Out($"[NoLightShadows] test room built: doorway at {doorX}, {cy + 1}, {doorZ}, "
                + $"candle at {cx}, {cy + 1}, {cz}");
        Log.Out("[NoLightShadows] midnight, camera placed facing the doorway");
        Log.Out("[NoLightShadows] expected: light spills from the doorway, the outside walls stay dark");
    }

    private static string RememberPath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "7DaysToDie", "nolightshadows-testroom.txt");

    private static void Remember(int x, int y, int z)
    {
        try { System.IO.File.WriteAllText(RememberPath, $"{x} {y} {z}"); }
        catch (System.Exception e) { Log.Warning("[NoLightShadows] could not note the room: " + e.Message); }
    }

    private static bool ReadRemembered(out int x, out int y, out int z)
    {
        x = y = z = 0;

        try
        {
            if (!System.IO.File.Exists(RememberPath)) return false;

            var parts = System.IO.File.ReadAllText(RememberPath).Split(' ');
            if (parts.Length != 3) return false;

            return int.TryParse(parts[0], out x)
                   && int.TryParse(parts[1], out y)
                   && int.TryParse(parts[2], out z);
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
