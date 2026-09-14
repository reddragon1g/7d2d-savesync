using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// Player-placed lights keep lighting the room, and stop casting shadows.
///
/// Written for a real base and a real measurement. On a laptop whose graphics card has lost its
/// fan, the same save at the same settings gives:
///
///     dynamic mesh ON,  shadows OFF  ->  steady 30 fps at 38% of the card
///     dynamic mesh OFF, shadows ON   ->  steady 30 fps at 38%
///     dynamic mesh ON,  shadows ON   ->  30, 22, 18, 16 and falling, card pinned at its floor
///
/// Two costs of a similar size, and only room for one. Giving up dynamic mesh means giving up how
/// player-built structures take damage and collapse, which is a fundamental part of this game and
/// the wrong thing to trade away. Giving up shadows entirely means a world where nothing sits on
/// the ground, which reads as broken rather than cheap.
///
/// Neither is necessary, because the bill is not really "shadows" - it is shadows PER LIGHT.
/// QualitySettings.shadows is global, so turning it down turns the sun down with it, but the
/// expensive part in a lit base is that every light near the player renders its own shadow map,
/// and for a point light that is a cubemap: six renders, per light, per frame. A base with dozens
/// of lights in it pays that bill dozens of times over, concentrated exactly where it was built.
///
/// So this leaves the setting alone and turns the shadows off on the lights themselves. The sun
/// and the moon still cast, the world still looks right, and the lights still do the one job they
/// were placed for.
///
/// Every light block already carries this setting - BlockLight assigns light.shadows from
/// TileEntityLight.LightShadows, and the game's own light editor exposes it as a dropdown. This
/// does nothing a person could not do by hand; it just does it to every light at once, including
/// the ones placed tomorrow.
/// </summary>
public sealed class ModApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        Log.Out("[NoLightShadows] player-placed lights will not cast shadows; the sun is untouched");
        new Harmony(GetType().ToString()).PatchAll(Assembly.GetExecutingAssembly());
    }
}

/// <summary>
/// Patches the game's own per-light shadow decision, after it has made it.
///
/// A postfix on FrameUpdate rather than a one-off change when a light is placed, because the game
/// re-decides continuously: LightLOD turns shadows off for distant lights and back on as the
/// player approaches, so anything set once would be overwritten the moment somebody walked towards
/// it. Correcting the answer after the game has settled on it is the only place this holds.
///
/// Limited to bPlayerPlacedLight, which is the game's own flag for the lights a person put there.
/// Lights belonging to the world - vehicle headlights, the lights inside prefabs, anything a level
/// designer placed deliberately - are left exactly as they are.
/// </summary>
[HarmonyPatch(typeof(LightLOD))]
[HarmonyPatch(nameof(LightLOD.FrameUpdate))]
public static class LightLodNoShadows
{
    public static void Postfix(LightLOD __instance)
    {
        if (!__instance.bPlayerPlacedLight) return;

        var light = __instance.myLight;

        // Null while a light is being built or torn down, and a mod that throws inside a
        // per-frame path would be far worse than the cost it is trying to remove.
        if (light == null || light.shadows == LightShadows.None) return;

        light.shadows = LightShadows.None;
    }
}
