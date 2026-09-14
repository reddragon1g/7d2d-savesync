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

    private const string WallBlock = "terrStone";
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

        var playerPos = player.position;

        // Straight out along +Z rather than wherever the player happens to face, so the room's
        // position is known in advance and the camera can be pointed at it.
        var flat = Vector3.forward;

        // Far enough that the whole room is in shot, near enough to see detail on the near wall.
        var centre = playerPos + flat * 9f;

        int cx = Mathf.FloorToInt(centre.x);
        int cy = Mathf.FloorToInt(playerPos.y);
        int cz = Mathf.FloorToInt(centre.z);

        var wall = Block.GetBlockValue(WallBlock, true);
        var light = Block.GetBlockValue(LightBlock, true);
        var air = BlockValue.Air;

        if (wall.isair || light.isair)
        {
            Log.Error($"[NoLightShadows] test scene: could not find '{WallBlock}' or '{LightBlock}'");
            return;
        }

        Log.Out($"[NoLightShadows] building a test room at {cx}, {cy}, {cz}");

        // A hollow box, walls one block thick, tall enough to stand in.
        const int half = 3;
        const int height = 4;

        for (int x = cx - half; x <= cx + half; x++)
        for (int z = cz - half; z <= cz + half; z++)
        for (int y = cy; y <= cy + height; y++)
        {
            bool onShell = x == cx - half || x == cx + half
                           || z == cz - half || z == cz + half
                           || y == cy || y == cy + height;

            world.SetBlock(new Vector3i(x, y, z), onShell ? wall : air, true, true);
        }

        // The doorway, punched through the wall nearest the player so the inside is visible.
        var toPlayer = -flat;
        int doorX = cx + Mathf.RoundToInt(toPlayer.x * half);
        int doorZ = cz + Mathf.RoundToInt(toPlayer.z * half);

        world.SetBlock(new Vector3i(doorX, cy + 1, doorZ), air, true, true);
        world.SetBlock(new Vector3i(doorX, cy + 2, doorZ), air, true, true);

        // The light, on the floor in the middle, away from every wall.
        world.SetBlock(new Vector3i(cx, cy + 1, cz), light, true, true);

        // Face the room, and stand far enough back to see all of it. Without this the camera
        // points wherever the player happened to spawn looking, which the first attempt proved
        // is usually at a building somewhere else entirely.
        player.SetPosition(new Vector3(cx + 0.5f, cy + 1.5f, cz - 9f));
        player.SetRotationAndStopTurning(new Vector3(0f, 0f, 0f));

        Log.Out($"[NoLightShadows] test room built: doorway at {doorX}, {cy + 1}, {doorZ}, "
                + $"candle at {cx}, {cy + 1}, {cz}");
        Log.Out("[NoLightShadows] midnight, camera placed facing the doorway");
        Log.Out("[NoLightShadows] expected: light spills from the doorway, the outside walls stay dark");
    }
}
