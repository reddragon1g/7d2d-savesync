using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// Lights that stop at walls without costing anything per frame.
///
/// Measured on a real base, on a laptop whose graphics card has lost its fan, at the same settings
/// throughout:
///
///     dynamic mesh ON,  shadows OFF  ->  steady 30 fps at 38% of the card
///     dynamic mesh OFF, shadows ON   ->  steady 30 fps at 38%
///     dynamic mesh ON,  shadows ON   ->  30, 22.5, 18.3, 16.6 and falling, card at its floor
///
/// Two costs of a similar size and only room for one, so something had to go: either the world's
/// shadows, which makes everything look like it is floating, or the system that lets player-built
/// structures take damage and collapse, which is most of what this game is.
///
/// Neither, as it turns out. The bill is not shadows, it is shadows PER LIGHT - every light near
/// the player re-renders the world six times a frame to find out what it is shining at. In a base
/// with dozens of lights that is paid dozens of times over, which is why this shows up as "my base
/// is slow" on hardware that runs the rest of the game perfectly well.
///
/// A world of one-metre cubes does not need rendering to answer that question. VoxelShadow walks
/// the block array once per light and produces the same mask as a cookie; the light then needs no
/// shadow map at all, and nothing has to be recomputed until somebody builds something.
///
/// The order below matters and is the whole safety property: a light's shadows are only turned off
/// once its mask exists. A light with neither shines through walls.
/// </summary>
public sealed class ModApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        Log.Out("[NoLightShadows] player-placed lights will be masked from the blocks around them, "
                + "not by rendering shadow maps; the sun is untouched");

        new Harmony(GetType().ToString()).PatchAll(Assembly.GetExecutingAssembly());
    }
}

/// <summary>
/// Replaces a player-placed light's shadow map with a mask computed from the world.
///
/// A postfix on FrameUpdate rather than a change made once when a light is placed, because the
/// game re-decides continuously: LightLOD turns shadows off for distant lights and back on as the
/// player approaches, so anything set once would be undone by walking towards it.
///
/// Only lights the game itself flags as player-placed. Lights belonging to the world - vehicle
/// headlights, the lights inside prefabs, anything a level designer put somewhere deliberately -
/// are left exactly as they are, because they are not the ones there are hundreds of.
/// </summary>
[HarmonyPatch(typeof(LightLOD))]
[HarmonyPatch(nameof(LightLOD.FrameUpdate))]
public static class LightLodVoxelMask
{
    /// <summary>
    /// Switched off with -NoLightShadowsDisable, so the same scene can be photographed with and
    /// without the mask.
    ///
    /// A photograph of a lit wall means nothing on its own: it could be light bleeding through, or
    /// it could be a pale wall under moonlight. The only way to tell is the same scene, same time,
    /// same camera, with the one thing under test turned off.
    /// </summary>
    private static bool Disabled
    {
        get
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.StartsWith("-NoLightShadowsDisable", System.StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }

    public static void Postfix(LightLOD __instance)
    {
        if (Disabled) return;

        if (!__instance.bPlayerPlacedLight) return;

        var light = __instance.myLight;

        // Null while a light is being built or torn down. A mod that throws inside a per-frame
        // path would cost far more than the thing it is removing.
        if (light == null || !light.enabled) return;

        var world = GameManager.Instance == null ? null : GameManager.Instance.World;
        if (world == null) return;

        // Order is everything. Shadows come off only once the mask is on, so a light is never
        // left with neither - which would be a light shining straight through the wall.
        if (!LightMasks.Apply(light, world)) return;

        if (light.shadows != LightShadows.None) light.shadows = LightShadows.None;
    }
}

/// <summary>
/// Retires every mask when the world changes under them.
///
/// A mask is a picture of the walls at the moment it was built. Break a wall and the light should
/// now reach through the hole; build one and it should stop - and neither happens on its own,
/// because nothing about the light has changed.
///
/// Hooked at the point every block change passes through, so it covers building, mining, damage
/// and collapse without needing to know about any of them.
/// </summary>
[HarmonyPatch(typeof(World))]
[HarmonyPatch(nameof(World.SetBlocksRPC))]
[HarmonyPatch(new[] { typeof(List<BlockChangeInfo>) })]
public static class WorldBlockChanged
{
    public static void Postfix() => LightMasks.WorldChanged();
}

/// <summary>
/// Runs the self test once, when a world and a player exist.
///
/// Hung on the game's own update rather than on anything to do with lights, because the test has
/// to run whether or not a light happens to be nearby - and because the thing it checks is the
/// world's geometry, not a light's.
///
/// Does nothing at all unless asked for on the command line, and stops checking after the first
/// run, so the cost in a normal game is one boolean per frame.
/// </summary>
[HarmonyPatch(typeof(GameManager))]
[HarmonyPatch("gmUpdate")]
public static class SelfTestRunner
{
    private static bool _finished;

    public static void Postfix()
    {
        if (_finished) return;

        if (!SelfTest.Requested) { _finished = true; return; }

        var world = GameManager.Instance == null ? null : GameManager.Instance.World;
        var player = world == null ? null : world.GetPrimaryPlayer();

        // Not an error - the world simply is not ready yet. Try again next frame.
        if (world == null || player == null) return;

        // And a player existing is not the same as the ground under them existing. The first run
        // of this test reported every direction clear, including straight down, because the
        // chunks were still streaming and World.IsAir calls an unloaded chunk empty.
        if (!world.IsChunkAreaLoaded(player.position)) return;

        _finished = true;
        SelfTest.Run(world, player.position);

        // Something to photograph. Only when asked for, and only ever in a throwaway world - it
        // writes blocks into whatever save is loaded.
        if (TestScene.Requested)
            TestScene.Build(world, player);
    }
}

/// <summary>Drops every mask when a world is unloaded, so none survive into the next one.</summary>
[HarmonyPatch(typeof(GameManager))]
[HarmonyPatch(nameof(GameManager.Cleanup))]
public static class GameCleanup
{
    public static void Postfix() => LightMasks.Forget();
}
