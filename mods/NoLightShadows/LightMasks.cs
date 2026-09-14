using System.Collections.Generic;
using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// Decides which lights get a voxel mask, and paces the work so making them never costs a hitch.
///
/// Building a mask is about a millisecond. That is nothing once, and a disaster if fifty lights
/// come into range on the same frame - which is exactly what happens when somebody walks into
/// their own base. So a strict budget per frame: a light without a mask simply goes without one
/// until its turn, and a light without a mask behaves as it did before this mod existed.
///
/// Being slow to arrive is fine. Arriving all at once is the bug.
/// </summary>
public static class LightMasks
{
    /// <summary>
    /// Masks built per frame, at most.
    ///
    /// One. Walking into a room with thirty lights spreads their masks over half a second, which
    /// nobody can see, instead of a single frame that everybody can.
    /// </summary>
    public const int BudgetPerFrame = 1;

    /// <summary>
    /// How far a light may move, or its range change, before its mask is rebuilt.
    ///
    /// Lights do not usually move. This exists because they CAN - a light on a drawbridge or a
    /// vehicle - and a mask computed at the old position would shadow the light with walls that
    /// are no longer there.
    /// </summary>
    private const float MovedEnough = 0.5f;

    private sealed class Mask
    {
        public Cubemap Cookie;
        public Vector3 BuiltAt;
        public float BuiltRange;
        public int BuiltGeneration;
    }

    private static readonly Dictionary<int, Mask> Masks = new Dictionary<int, Mask>();

    private static int _frame = -1;
    private static int _builtThisFrame;

    /// <summary>
    /// Bumped when the world changes, to retire every mask built before it.
    ///
    /// Deliberately blunt: one counter for the whole world rather than tracking which lights are
    /// near which broken block. Rebuilding a few masks that did not need it costs a millisecond
    /// each, spread over frames; failing to rebuild one that did leaves a shadow cast by a wall
    /// somebody has already torn down, which is visible and looks like a bug.
    /// </summary>
    public static int Generation { get; private set; }

    public static void WorldChanged() => Generation++;

    public static void Forget()
    {
        foreach (var mask in Masks.Values)
            if (mask.Cookie != null) Object.Destroy(mask.Cookie);

        Masks.Clear();
        Generation = 0;
    }

    /// <summary>
    /// Gives this light its mask, building one if there is budget and it needs it.
    ///
    /// Returns true when the light is masked and can safely have its shadows turned off. False
    /// means "not yet" - and the caller must then leave the light alone, because a light with no
    /// shadow map AND no mask is a light that shines through walls.
    /// </summary>
    public static bool Apply(Light light, World world)
    {
        if (light == null || world == null) return false;

        // A light with no reach cannot escape anything, and would divide by zero below.
        if (light.range <= 0.01f) return false;

        int id = light.GetInstanceID();
        var position = light.transform.position;

        if (Masks.TryGetValue(id, out var existing) && existing.Cookie != null)
        {
            bool stale = existing.BuiltGeneration != Generation
                         || (existing.BuiltAt - position).sqrMagnitude > MovedEnough * MovedEnough
                         || Mathf.Abs(existing.BuiltRange - light.range) > MovedEnough;

            if (!stale)
            {
                // Reassigned every time rather than assumed: something else - the game, another
                // mod - may have cleared it, and a light whose cookie has quietly gone is a light
                // shining through walls again.
                if (light.cookie != existing.Cookie) light.cookie = existing.Cookie;
                return true;
            }
        }

        if (!Spend()) return false;

        // The light's own position is in the scene's coordinates; the blocks are in the world's,
        // which move as the player travels. Origin is the offset between them.
        var inWorld = position - Origin.position;

        var cookie = VoxelShadow.Build(world, inWorld, light.range, VoxelShadow.DefaultResolution);

        if (existing?.Cookie != null) Object.Destroy(existing.Cookie);

        Masks[id] = new Mask
        {
            Cookie = cookie,
            BuiltAt = position,
            BuiltRange = light.range,
            BuiltGeneration = Generation,
        };

        light.cookie = cookie;
        return true;
    }

    /// <summary>Takes this frame's allowance, or refuses.</summary>
    private static bool Spend()
    {
        int now = Time.frameCount;
        if (now != _frame) { _frame = now; _builtThisFrame = 0; }

        if (_builtThisFrame >= BudgetPerFrame) return false;

        _builtThisFrame++;
        return true;
    }
}
