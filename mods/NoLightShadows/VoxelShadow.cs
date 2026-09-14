using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// A light's shadow, worked out from the blocks instead of by rendering the world six times.
///
/// A shadow map is expensive for a reason that has nothing to do with shadows being hard. Unity
/// finds out what blocks a light by re-drawing the scene from the light's point of view - and for
/// a point light that is six times, once per face of a cube, every frame, for every light near the
/// player. A base full of lights pays that bill over and over to rediscover something that has not
/// changed since it was built.
///
/// In this game we already know the answer. The world is one-metre cubes in an array, and asking
/// "is there a block at this position" is a lookup. So the mask can be computed directly, once,
/// and handed to the light as a cookie - a texture Unity multiplies the light by, per direction.
/// Functionally that is a shadow map. The difference is that it is computed from data rather than
/// from rendering, and never has to be computed again until somebody builds something.
///
/// The result: light.shadows stays None, so there is no shadow map and no per-frame cost at all,
/// and the light still stops at the walls.
///
/// Two honest limits. It shadows the world, not things moving through it - a zombie walking past
/// one of these lights casts nothing. And it is only as fine as its resolution; at the default a
/// texel covers roughly one block at the edge of the light's reach, which is about as much detail
/// as a world made of cubes can carry anyway.
/// </summary>
public static class VoxelShadow
{
    /// <summary>
    /// Texels per cube face. 16 puts roughly one texel per block at the edge of a typical light.
    ///
    /// Going higher costs generation time and memory for detail the world does not have: the
    /// geometry casting these shadows is made of metre cubes, so a finer mask mostly resolves the
    /// stair-stepping of the cubes themselves.
    /// </summary>
    public const int DefaultResolution = 16;

    /// <summary>
    /// How far apart the samples are along a ray, in blocks.
    ///
    /// Half a block rather than a whole one so a ray cannot pass through the corner of a block
    /// without noticing it, which shows up as pinholes of light through solid walls.
    /// </summary>
    private const float StepBlocks = 0.5f;

    /// <summary>
    /// Starts this far from the light before sampling.
    ///
    /// A light mounted ON a wall is inside, or touching, the very block it is attached to. Sampling
    /// from zero would find that block immediately in every direction and mask the light out
    /// completely - a lamp that lights nothing, which is exactly the sort of result that looks like
    /// the mod is broken rather than working.
    /// </summary>
    private const float StartOffsetBlocks = 1.2f;

    /// <summary>
    /// Builds the cube of visibility around a point.
    ///
    /// White where the light gets out, black where a block stops it. Returned ready to hand to a
    /// light as its cookie.
    /// </summary>
    public static Cubemap Build(World world, Vector3 worldPos, float range, int resolution)
    {
        var cube = new Cubemap(resolution, TextureFormat.R8, mipChain: false)
        {
            // Clamped, because the faces are sampled right to their edges and wrapping would fetch
            // from the opposite side of the cube - light leaking out of the wall behind it.
            wrapMode = TextureWrapMode.Clamp,

            // Filtered, so the mask fades over a texel instead of ending in a hard staircase. The
            // blur is what makes a 16-pixel face look like a soft shadow rather than a mistake.
            filterMode = FilterMode.Bilinear,
            anisoLevel = 0,
        };

        var faces = new[]
        {
            CubemapFace.PositiveX, CubemapFace.NegativeX,
            CubemapFace.PositiveY, CubemapFace.NegativeY,
            CubemapFace.PositiveZ, CubemapFace.NegativeZ,
        };

        // Color rather than Color32: Cubemap offers SetPixels but not SetPixels32.
        var pixels = new Color[resolution * resolution];

        foreach (var face in faces)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    // Texel centres, mapped to -1..1 across the face.
                    float u = (x + 0.5f) / resolution * 2f - 1f;
                    float v = (y + 0.5f) / resolution * 2f - 1f;

                    var direction = DirectionFor(face, u, v);
                    bool clear = IsClear(world, worldPos, direction, range);

                    pixels[y * resolution + x] = clear ? Color.white : Color.black;
                }
            }

            cube.SetPixels(pixels, face);
        }

        cube.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return cube;
    }

    /// <summary>
    /// The direction a texel on a cube face points.
    ///
    /// This is the convention Unity samples cubemaps with, and getting any sign wrong produces a
    /// light whose shadows are mirrored or rotated - which looks like nonsense rather than like a
    /// bug, so it is written out face by face rather than cleverly.
    /// </summary>
    public static Vector3 DirectionFor(CubemapFace face, float u, float v) => face switch
    {
        CubemapFace.PositiveX => new Vector3(1f, -v, -u).normalized,
        CubemapFace.NegativeX => new Vector3(-1f, -v, u).normalized,
        CubemapFace.PositiveY => new Vector3(u, 1f, v).normalized,
        CubemapFace.NegativeY => new Vector3(u, -1f, -v).normalized,
        CubemapFace.PositiveZ => new Vector3(u, -v, 1f).normalized,
        _ => new Vector3(-u, -v, -1f).normalized,
    };

    /// <summary>
    /// Walks one ray through the blocks and says whether it got to the end.
    ///
    /// Stepping rather than a proper grid traversal on purpose: a step of half a block cannot skip
    /// one, the ray is at most a few dozen samples long, and each sample is an array lookup. The
    /// simpler loop is easier to be sure of, and this runs once per light rather than per frame.
    /// </summary>
    public static bool RayIsClear(World world, Vector3 from, Vector3 direction, float range)
        => IsClear(world, from, direction, range);

    private static bool IsClear(World world, Vector3 from, Vector3 direction, float range)
    {
        for (float distance = StartOffsetBlocks; distance <= range; distance += StepBlocks)
        {
            var at = from + direction * distance;

            // Floor rather than round: block coordinates name the cube a position falls inside.
            int x = Mathf.FloorToInt(at.x);
            int y = Mathf.FloorToInt(at.y);
            int z = Mathf.FloorToInt(at.z);

            // Outside the loaded world is not a wall. Treating it as one would black out every
            // light near the edge of what is loaded, which moves as the player walks.
            if (y < 0 || y >= 256) continue;

            if (!world.IsAir(x, y, z)) return false;
        }

        return true;
    }
}
