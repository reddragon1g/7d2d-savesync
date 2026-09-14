using UnityEngine;

namespace NoLightShadows;

/// <summary>
/// Checks the mask against something already known to be true, and says so in the log.
///
/// The riskiest thing in this mod is a convention rather than a calculation: which direction each
/// texel of a cube face points. Get a sign wrong and the shadows come out mirrored or rotated, and
/// the result does not look like a bug - it looks like the lighting is simply strange, which is
/// the sort of thing that survives for weeks because nobody can say what is wrong with it.
///
/// Testing it normally would mean somebody building a sealed room, placing a light and looking at
/// it. But the world already contains a test fixture that needs no building: there is ground below
/// and sky above. Straight down from a standing player must be blocked. Straight up must be clear.
/// If those two come back the other way round, the convention is upside down, and it says so in
/// one line of log that can be read from another machine.
///
/// It also samples the whole cube, because "up is clear and down is blocked" would still pass if
/// the horizontal faces were swapped with each other - so the horizontal faces are reported too,
/// and in open ground they should all be largely clear.
/// </summary>
public static class SelfTest
{
    private const string Flag = "-NoLightShadowsSelfTest";

    private static bool _ran;

    /// <summary>True when the flag was passed on the command line.</summary>
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
    /// Casts the same rays the mask is built from and reports how much of each face got out.
    ///
    /// Deliberately re-using DirectionFor rather than testing a copy of it: a test that agrees
    /// with its own private idea of which way is up proves nothing about the code that ships.
    /// </summary>
    public static void Run(World world, Vector3 standingAt)
    {
        if (_ran) return;
        _ran = true;

        // Head height and a realistic reach, so this measures the situation a real light is in
        // rather than a special case.
        var from = standingAt + Vector3.up * 1.5f;
        const float range = 10f;
        const int resolution = 8;

        Log.Out($"[NoLightShadows] self test at {from.x:0.0}, {from.y:0.0}, {from.z:0.0}");

        var faces = new[]
        {
            CubemapFace.PositiveX, CubemapFace.NegativeX,
            CubemapFace.PositiveY, CubemapFace.NegativeY,
            CubemapFace.PositiveZ, CubemapFace.NegativeZ,
        };

        float up = 0, down = 0;

        foreach (var face in faces)
        {
            int clear = 0, total = 0;

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float u = (x + 0.5f) / resolution * 2f - 1f;
                    float v = (y + 0.5f) / resolution * 2f - 1f;

                    if (VoxelShadow.RayIsClear(world, from, VoxelShadow.DirectionFor(face, u, v), range))
                        clear++;

                    total++;
                }
            }

            float fraction = total == 0 ? 0 : (float)clear / total;
            if (face == CubemapFace.PositiveY) up = fraction;
            if (face == CubemapFace.NegativeY) down = fraction;

            Log.Out($"[NoLightShadows]   {face,-12} {fraction * 100:0}% of it gets out");
        }

        // The fixture. Standing on the ground, down is into the ground and up is into the sky.
        if (up > 0.6f && down < 0.4f)
        {
            Log.Out("[NoLightShadows] PASS - up is open and down is blocked, so the cube is the "
                    + "right way round");
        }
        else if (down > 0.6f && up < 0.4f)
        {
            Log.Error("[NoLightShadows] FAIL - down is open and up is blocked. The cube is UPSIDE "
                      + "DOWN: the sign on the vertical faces in DirectionFor is wrong.");
        }
        else
        {
            Log.Warning($"[NoLightShadows] INCONCLUSIVE - up {up * 100:0}%, down {down * 100:0}%. "
                        + "Run this standing outside on open ground; indoors or underground both "
                        + "directions are blocked and this proves nothing either way.");
        }
    }
}
